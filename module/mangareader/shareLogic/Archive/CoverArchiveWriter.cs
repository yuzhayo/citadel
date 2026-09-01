using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Module.Mangareader.Archive;

/// <summary>
/// Byte-level identity of one preserved original entry, captured while the
/// rewritten archive is built and re-verified before the commit point.
/// </summary>
public sealed record OriginalEntryManifest(string FullName, long Length, byte[] Sha256);

/// <summary>
/// The complete ZIP/CBZ bake support. The generated cover is always written
/// as the first logical entry under <see cref="GeneratedCoverEntryName"/>;
/// every original non-generated entry keeps its relative path and, where
/// possible, its timestamp. Nothing here trusts the source archive: the PNG
/// is validated before any mutation and the rewritten archive is reopened
/// and verified afterwards.
/// </summary>
public static class CoverArchiveWriter
{
    public const string GeneratedCoverEntryName = "!00000-citadel-cover.png";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IhdrType = "IHDR"u8.ToArray();
    private static readonly byte[] IendType = "IEND"u8.ToArray();

    /// <summary>
    /// Validates the PNG before any archive mutation starts: signature,
    /// a well-formed IHDR as the first chunk, and an IEND terminator reached
    /// by walking chunk boundaries (not by searching raw bytes).
    /// </summary>
    public static void ValidateCoverPng(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
            throw new InvalidDataException("The cover PNG is empty.");
        if (pngBytes.Length < 8 + 12 + 13)
            throw new InvalidDataException("The cover PNG is too small to be a valid image.");
        if (!pngBytes.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new InvalidDataException("The cover PNG signature is invalid.");
        if (!pngBytes.AsSpan(12, 4).SequenceEqual(IhdrType))
            throw new InvalidDataException("The cover PNG does not start with an IHDR chunk.");

        var offset = 8;
        while (offset + 12 <= pngBytes.Length)
        {
            var chunkLength = (pngBytes[offset] << 24)
                | (pngBytes[offset + 1] << 16)
                | (pngBytes[offset + 2] << 8)
                | pngBytes[offset + 3];
            var chunkType = pngBytes.AsSpan(offset + 4, 4);
            if (chunkLength < 0 || offset + 12 + chunkLength > pngBytes.Length)
                throw new InvalidDataException("The cover PNG is truncated inside a chunk.");
            if (chunkType.SequenceEqual(IendType)) return;
            offset += 12 + chunkLength;
        }

        throw new InvalidDataException("The cover PNG has no IEND terminator.");
    }

    /// <summary>
    /// Rewrites <paramref name="sourcePath"/> into
    /// <paramref name="destinationPath"/> with the generated cover as the
    /// first entry. Returns a byte-level manifest of every preserved
    /// original entry — relative name, length and SHA-256 captured during
    /// the copy — for verification before the commit point.
    /// </summary>
    public static IReadOnlyList<OriginalEntryManifest> WriteBakedZip(
        string sourcePath,
        string destinationPath,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        using var sourceFile = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var sourceArchive = new ZipArchive(sourceFile, ZipArchiveMode.Read, leaveOpen: false);
        using var destinationFile = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan);
        using var destinationArchive = new ZipArchive(
            destinationFile,
            ZipArchiveMode.Create,
            leaveOpen: false);

        var cover = destinationArchive.CreateEntry(
            GeneratedCoverEntryName,
            CompressionLevel.NoCompression);
        using (var coverStream = cover.Open())
        {
            coverStream.Write(pngBytes, 0, pngBytes.Length);
        }

        var manifest = new List<OriginalEntryManifest>();
        var buffer = new byte[128 * 1024];
        foreach (var sourceEntry in sourceArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceEntry.Name.Length == 0
                || string.Equals(
                    sourceEntry.FullName,
                    GeneratedCoverEntryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationEntry = destinationArchive.CreateEntry(
                sourceEntry.FullName,
                CompressionLevel.NoCompression);
            TryCopyTimestamp(sourceEntry, destinationEntry);

            long copiedLength = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = sourceEntry.Open())
            using (var output = destinationEntry.Open())
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    hash.AppendData(buffer, 0, read);
                    copiedLength += read;
                    output.Write(buffer, 0, read);
                }
            }

            manifest.Add(new OriginalEntryManifest(
                sourceEntry.FullName,
                copiedLength,
                hash.GetHashAndReset()));
        }

        return manifest;
    }

    /// <summary>
    /// Reopens a baked archive and verifies the full cover contract: it is
    /// readable, exactly one generated cover exists, the cover is the first
    /// entry, its bytes match the requested cover, and every preserved
    /// original entry is present with its exact length and SHA-256 — names
    /// alone never pass, so a corrupt page in the rewritten archive is
    /// caught before the commit point.
    /// </summary>
    public static void ValidateBakedZip(
        string bakedPath,
        byte[] expectedCoverBytes,
        IReadOnlyCollection<OriginalEntryManifest> preservedEntries,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(
            bakedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

        var entries = archive.Entries;
        var coverCount = 0;
        var entriesByPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        byte[]? coverBytes = null;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entriesByPath[entry.FullName] = entry;
            if (string.Equals(
                    entry.FullName,
                    GeneratedCoverEntryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                coverCount++;
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                coverBytes = memory.ToArray();
            }
        }

        if (coverCount != 1)
        {
            throw new InvalidDataException(
                $"The rewritten chapter contains {coverCount} generated covers; exactly one is required.");
        }

        if (entries.Count == 0
            || !string.Equals(entries[0].FullName, GeneratedCoverEntryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The generated cover is not the first entry of the rewritten chapter.");
        }

        if (coverBytes is null || !coverBytes.AsSpan().SequenceEqual(expectedCoverBytes))
        {
            throw new InvalidDataException(
                "The generated cover bytes do not match the requested cover.");
        }

        foreach (var manifest in preservedEntries)
        {
            if (!entriesByPath.TryGetValue(manifest.FullName, out var entry))
            {
                throw new InvalidDataException(
                    $"The original entry \"{manifest.FullName}\" is missing from the rewritten chapter.");
            }

            if (entry.Length != manifest.Length)
            {
                throw new InvalidDataException(
                    $"The original entry \"{manifest.FullName}\" changed size in the rewritten chapter.");
            }

            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }

            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(manifest.Sha256))
            {
                throw new InvalidDataException(
                    $"The original entry \"{manifest.FullName}\" is corrupt in the rewritten chapter.");
            }
        }
    }

    private static void TryCopyTimestamp(ZipArchiveEntry source, ZipArchiveEntry destination)
    {
        try
        {
            destination.LastWriteTime = source.LastWriteTime;
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }
}
