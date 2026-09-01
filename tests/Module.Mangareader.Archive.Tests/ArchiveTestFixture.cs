using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Module.Mangareader.Archive;

namespace Module.Mangareader.Archive.Tests;

/// <summary>
/// Isolated temporary fixtures. No test may touch a real manga folder or
/// terminate a real application.
/// </summary>
public abstract class ArchiveTestFixture : IDisposable
{
    protected string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "citadel-archive-tests",
        Guid.NewGuid().ToString("N"));

    protected ArchiveTestFixture() => Directory.CreateDirectory(Root);

    protected string ChapterPath(string name) => Path.Combine(Root, name);

    protected string BackupRoot()
    {
        var path = Path.Combine(Root, "cover-backups");
        Directory.CreateDirectory(path);
        return path;
    }

    protected ArchiveReplacementTransaction CreateTransaction(string? backupRoot = null) =>
        new(
            new LatestCoverBackupStore(backupRoot ?? BackupRoot()),
            new NullLockCoordinator());

    protected static void CreateZip(
        string path,
        params (string Name, byte[] Bytes)[] entries)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    protected static IReadOnlyList<(string Name, byte[] Bytes)> ReadZipEntries(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        return archive.Entries
            .Select(entry =>
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return (entry.FullName, memory.ToArray());
            })
            .ToArray();
    }

    protected static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    protected static string Sha256HexOfFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// A structurally valid 1x1 RGBA PNG built with correct chunk CRCs.
    /// </summary>
    protected static byte[] MinimalPng() => BuildPng(width: 1, height: 1);

    /// <summary>
    /// A second valid PNG whose bytes differ from <see cref="MinimalPng"/>.
    /// </summary>
    protected static byte[] AlternativePng() => BuildPng(width: 2, height: 1);

    private static byte[] BuildPng(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA
        WritePngChunk(png, "IHDR"u8.ToArray(), ihdr);

        // Stored zlib stream placeholder; the archive contract under test
        // walks chunk structure, it never decodes pixels.
        WritePngChunk(png, "IDAT"u8.ToArray(), [0x78, 0x01, 0x01, 0x00, 0x00, 0xFE, 0xFF, 0x00]);
        WritePngChunk(png, "IEND"u8.ToArray(), []);
        return png.ToArray();
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WritePngChunk(Stream output, byte[] type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crc = Crc32(type, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        output.Write(crcBytes);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var block in new[] { type, data })
        {
            foreach (var value in block)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    protected static byte[] Rar4Content()
    {
        byte[] marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        var bytes = new byte[64];
        Array.Copy(marker, bytes, marker.Length);
        return bytes;
    }

    protected static byte[] Rar5Content()
    {
        byte[] marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
        var bytes = new byte[64];
        Array.Copy(marker, bytes, marker.Length);
        return bytes;
    }

    protected static byte[] SevenZipContent()
    {
        byte[] marker = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        var bytes = new byte[64];
        Array.Copy(marker, bytes, marker.Length);
        return bytes;
    }

    protected static byte[] PdfContent() =>
        "%PDF-1.7\n%truncated test body\n"u8.ToArray();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class NullLockCoordinator : IArchiveLockCoordinator
{
    public IReadOnlyList<ReleasedArchiveProcess> ReleaseForReplacement(
        string archivePath,
        CancellationToken cancellationToken) =>
        Array.Empty<ReleasedArchiveProcess>();
}
