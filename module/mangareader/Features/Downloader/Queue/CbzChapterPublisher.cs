using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Module.Mangareader.Archive;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>
/// The provenance entry written inside every published CBZ at
/// <c>META-INF/citadel-source.json</c>. No loose metadata file is ever placed
/// beside manga files.
/// </summary>
public sealed record CbzSourceManifest(
    int Version,
    string Provider,
    string TitleId,
    string ChapterId,
    string ChapterNumber,
    string GroupId,
    string GroupName,
    int PageCount,
    string ManifestHash);

/// <summary>One staged page ready to be packaged, in manifest order.</summary>
public sealed record StagedPage(int Ordinal, string AbsolutePath, string Format);

public sealed record PublicationOutcome(
    bool Published,
    string? Path,
    string? ConflictReason,
    IReadOnlyList<ReleasedArchiveProcess> ReleasedProcesses,
    string? BackupPath,
    IReadOnlyList<string> Warnings)
{
    public static PublicationOutcome Conflict(string reason) =>
        new(false, null, reason, [], null, []);
}

/// <summary>
/// Builds one ordinary ZIP-compatible CBZ from validated staging and commits it
/// atomically. Nothing is visible at the final path until every check has
/// passed, and a partial file is never discoverable by the Library scanner
/// because its temporary name does not carry a chapter extension.
/// </summary>
public sealed class CbzChapterPublisher
{
    public const int SourceManifestVersion = 1;
    public const string SourceManifestEntryName = "META-INF/citadel-source.json";

    private readonly ArchiveValidator _validator;
    private readonly IArchiveLockCoordinator _locks;
    private readonly LatestCoverBackupStore _backups;

    public CbzChapterPublisher(
        ArchiveValidator? validator = null,
        IArchiveLockCoordinator? locks = null,
        LatestCoverBackupStore? backups = null)
    {
        _validator = validator ?? new ArchiveValidator();
        _locks = locks ?? new ArchiveLockCoordinator();
        _backups = backups ?? new LatestCoverBackupStore();
    }

    public Task<PublicationOutcome> PublishAsync(
        RemoteChapterIdentity identity,
        string groupDisplayName,
        string manifestHash,
        IReadOnlyList<StagedPage> pages,
        DownloadTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(target);
        return Task.Run(
            () => Publish(identity, groupDisplayName, manifestHash, pages, target, cancellationToken),
            cancellationToken);
    }

    private PublicationOutcome Publish(
        RemoteChapterIdentity identity,
        string groupDisplayName,
        string manifestHash,
        IReadOnlyList<StagedPage> pages,
        DownloadTarget target,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        // 1. Completeness against the manifest: every ordinal exactly once.
        if (pages.Count == 0)
        {
            return PublicationOutcome.Conflict("Tidak ada page untuk dipublikasikan.");
        }

        for (var index = 0; index < pages.Count; index++)
        {
            if (pages[index].Ordinal != index)
            {
                return PublicationOutcome.Conflict(
                    $"Urutan page tidak lengkap: diharapkan ordinal {index}, ditemukan {pages[index].Ordinal}.");
            }

            if (!File.Exists(pages[index].AbsolutePath))
            {
                return PublicationOutcome.Conflict(
                    $"Page {index} tidak ada di staging: {pages[index].AbsolutePath}");
            }
        }

        // 2. Collision policy before any write: an existing file is only
        //    replaceable when its own provenance proves it is the same chapter.
        Directory.CreateDirectory(target.FolderPath);
        var finalPath = target.FilePath;
        var replacing = File.Exists(finalPath);
        if (replacing)
        {
            var existing = ReadSourceManifest(finalPath);
            if (existing is null)
            {
                return PublicationOutcome.Conflict(
                    $"Target sudah ada tanpa provenance Citadel dan tidak akan ditimpa: {finalPath}");
            }

            if (!SameChapter(existing, identity))
            {
                return PublicationOutcome.Conflict(
                    $"Target sudah dimiliki chapter lain ({existing.Provider}/{existing.ChapterId}/group {existing.GroupId}); tidak ditimpa dan tidak dibuat salinan '(2)'.");
            }
        }

        var sourceManifest = new CbzSourceManifest(
            SourceManifestVersion,
            identity.SourceId,
            identity.Title.TitleId,
            identity.ChapterId,
            identity.ChapterNumber,
            identity.Group.GroupId,
            groupDisplayName,
            pages.Count,
            manifestHash);

        // 3. Build the payload in the target folder under a non-discoverable
        //    temporary name, so the final rename stays on one volume.
        var temporaryPath = Path.Combine(
            target.FolderPath,
            Path.GetFileNameWithoutExtension(finalPath) + $".partial.{Guid.NewGuid():N}.tmp");

        string? backupCandidate = null;
        try
        {
            WriteZip(temporaryPath, pages, sourceManifest, cancellationToken);

            // 4. Validate the built archive before it can become the final file.
            var validation = _validator.Validate(temporaryPath);
            if (validation.State != ArchiveState.Healthy)
            {
                return PublicationOutcome.Conflict(
                    $"Arsip yang dibangun tidak sehat ({validation.State}): {validation.Detail}");
            }

            VerifyArchiveEntries(temporaryPath, pages.Count, sourceManifest);

            IReadOnlyList<ReleasedArchiveProcess> released = [];
            string? backupPath = null;
            if (replacing)
            {
                // Existing archive: reuse the shared lock/backup policy, and
                // keep only the latest backup.
                backupCandidate = _backups.PrepareBackupCandidate(finalPath, cancellationToken);
                released = _locks.ReleaseForReplacement(finalPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: replacing);

            if (backupCandidate is not null)
            {
                var promotion = _backups.PromoteCandidate(backupCandidate);
                backupCandidate = null;
                backupPath = promotion.BackupPath;
                warnings.AddRange(promotion.CleanupWarnings);
            }

            return new PublicationOutcome(
                true,
                finalPath,
                null,
                released,
                backupPath,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return PublicationOutcome.Conflict(
                $"Publikasi gagal sebelum commit: {exception.GetBaseException().Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
            if (backupCandidate is not null)
            {
                _backups.DiscardCandidate(backupCandidate);
            }
        }
    }

    private static void WriteZip(
        string path,
        IReadOnlyList<StagedPage> pages,
        CbzSourceManifest manifest,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var padding = pages.Count.ToString().Length < 3 ? 3 : pages.Count.ToString().Length;

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = string.IsNullOrWhiteSpace(page.Format) ? "bin" : page.Format.ToLowerInvariant();
            var entryName = (page.Ordinal + 1).ToString().PadLeft(padding, '0') + "." + extension;
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using var source = File.OpenRead(page.AbsolutePath);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var provenance = archive.CreateEntry(SourceManifestEntryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(provenance.Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Reopens the built archive and checks it the way a reader will: the page
    /// count matches, every image entry actually decodes, no entry is empty,
    /// and the embedded provenance is the one this job intended.
    /// </summary>
    private static void VerifyArchiveEntries(
        string path,
        int expectedPages,
        CbzSourceManifest expectedManifest)
    {
        using var archive = ZipFile.OpenRead(path);
        var imageEntries = archive.Entries
            .Where(entry => !string.Equals(entry.FullName, SourceManifestEntryName, StringComparison.Ordinal))
            .ToList();

        if (imageEntries.Count != expectedPages)
        {
            throw new InvalidDataException(
                $"Arsip berisi {imageEntries.Count} page, diharapkan {expectedPages}.");
        }

        foreach (var entry in imageEntries)
        {
            if (entry.Length == 0)
            {
                throw new InvalidDataException($"Entri '{entry.FullName}' kosong.");
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)Math.Min(entry.Length, 32 * 1024 * 1024));
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();

            var decoder = BitmapDecoder.Create(
                new MemoryStream(bytes, writable: false),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException($"Entri '{entry.FullName}' tidak punya frame gambar.");
            }
        }

        var provenance = archive.GetEntry(SourceManifestEntryName)
            ?? throw new InvalidDataException("Provenance entry tidak ada di arsip.");
        using var provenanceStream = provenance.Open();
        var embedded = JsonSerializer.Deserialize<CbzSourceManifest>(provenanceStream);
        if (embedded is null
            || embedded.PageCount != expectedManifest.PageCount
            || !string.Equals(embedded.ChapterId, expectedManifest.ChapterId, StringComparison.Ordinal)
            || !string.Equals(embedded.GroupId, expectedManifest.GroupId, StringComparison.Ordinal)
            || !string.Equals(embedded.ManifestHash, expectedManifest.ManifestHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Provenance di dalam arsip tidak cocok dengan identitas job.");
        }
    }

    private static CbzSourceManifest? ReadSourceManifest(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(SourceManifestEntryName);
            if (entry is null) return null;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<CbzSourceManifest>(stream);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or NotSupportedException)
        {
            // An unreadable existing target is treated as unknown provenance,
            // which is a conflict rather than a licence to overwrite.
            return null;
        }
    }

    private static bool SameChapter(CbzSourceManifest existing, RemoteChapterIdentity identity) =>
        string.Equals(existing.Provider, identity.SourceId, StringComparison.Ordinal)
        && string.Equals(existing.ChapterId, identity.ChapterId, StringComparison.Ordinal)
        && string.Equals(existing.GroupId, identity.Group.GroupId, StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
