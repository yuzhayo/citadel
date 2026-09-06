using System.IO;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// A user-confirmed mapping from one remote title identity to one local folder.
/// Display-title equality is never evidence of identity, so the mapping is keyed
/// on provider identity and confirmed once by the user.
/// </summary>
public sealed record SourceTitleMapping(
    string SourceId,
    string TitleId,
    string TitleHid,
    string FolderName,
    DateTimeOffset ConfirmedUtc);

/// <summary>One chapter this Downloader already published.</summary>
public sealed record PublishedChapterRecord(
    string SourceId,
    string TitleHid,
    string ChapterId,
    string GroupId,
    string FileName,
    DateTimeOffset PublishedUtc);

/// <summary>
/// Owns confirmed remote-identity mappings and published chapter identities.
/// Persisted beside the queue, never inside a manga folder, and never a second
/// authority for job state.
/// </summary>
public sealed class DownloadSourceIndex
{
    public const int SchemaVersion = 1;
    private const long MaximumFileBytes = 8 * 1024 * 1024;

    private readonly object _gate;
    private readonly string _path;

    public DownloadSourceIndex(string? directory = null)
    {
        _path = Path.Combine(directory ?? DownloaderJson.DefaultRoot(), "source-index.json");
        _gate = DownloaderJson.GateFor(_path);
    }

    public string FilePath => _path;

    public IndexLoadResult Load()
    {
        lock (_gate)
        {
            var document = DownloaderJson.ReadBounded<IndexDocument>(
                _path,
                MaximumFileBytes,
                out var warning);
            if (document is null)
            {
                return new IndexLoadResult([], [], warning, null);
            }

            if (document.Version != SchemaVersion)
            {
                return new IndexLoadResult(
                    [],
                    [],
                    warning ?? $"source-index.json schema version {document.Version} tidak dikenali; dipertahankan.",
                    document.Version);
            }

            return new IndexLoadResult(
                document.Mappings ?? [],
                document.Published ?? [],
                warning,
                null);
        }
    }

    /// <summary>
    /// Reuses an existing mapping only while its folder still exists. A mapping
    /// whose folder is gone is reported as absent so the user confirms a target
    /// again instead of publishing into a recreated folder blindly.
    /// </summary>
    public SourceTitleMapping? FindUsableMapping(
        string libraryRoot,
        RemoteTitleIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(identity);

        var loaded = Load();
        var mapping = loaded.Mappings.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceId, identity.SourceId, StringComparison.Ordinal)
            && string.Equals(candidate.TitleId, identity.TitleId, StringComparison.Ordinal));
        if (mapping is null) return null;

        // A persisted folder name is not trusted input: containing it here means an
        // unsafe value makes the mapping unusable instead of pointing a later write
        // outside the Library, and the caller falls back to confirming a target
        // again — the same path it already takes for a folder that disappeared.
        if (!DownloaderPathContainment.TryResolve(
                libraryRoot,
                mapping.FolderName,
                fileName: null,
                out var folderPath,
                out _))
        {
            return null;
        }

        return Directory.Exists(folderPath) ? mapping : null;
    }

    /// <summary>
    /// The deterministic folder for one remote title: an existing usable confirmed
    /// mapping when there is one, otherwise the sanitized display name. A queue
    /// target, a Lister probe and an Auto Cover destination all resolve through
    /// this, so the three always name the same folder for the same title.
    ///
    /// It never prompts. Choosing to claim an unrelated same-named folder is an
    /// interactive decision that belongs to queueing alone.
    /// </summary>
    public string DeterministicFolderName(string libraryRoot, RemoteTitleSummary title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(title);

        var existing = FindUsableMapping(libraryRoot, title.Identity);
        return existing?.FolderName ?? DownloadQueueFeature.SanitizeFolder(title.DisplayName);
    }

    public void ConfirmMapping(SourceTitleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        lock (_gate)
        {
            var document = ReadForWrite();
            var mappings = (document.Mappings ?? []).ToList();
            mappings.RemoveAll(candidate =>
                string.Equals(candidate.SourceId, mapping.SourceId, StringComparison.Ordinal)
                && string.Equals(candidate.TitleId, mapping.TitleId, StringComparison.Ordinal));
            mappings.Add(mapping);
            document.Mappings = mappings;
            DownloaderJson.WriteAtomic(_path, document);
        }
    }

    /// <summary>The recorded publication for one chapter identity, or null.</summary>
    public PublishedChapterRecord? FindPublished(DownloadJobIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var loaded = Load();
        return loaded.Published.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceId, identity.SourceId, StringComparison.Ordinal)
            && string.Equals(candidate.TitleHid, identity.TitleHid, StringComparison.Ordinal)
            && string.Equals(candidate.ChapterId, identity.ChapterId, StringComparison.Ordinal)
            && string.Equals(candidate.GroupId, identity.GroupId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether one chapter is still published. The record alone is not enough: a CBZ
    /// the user deleted leaves its entry behind, and trusting that entry would refuse
    /// a download the Library no longer has — silently, because the local probe
    /// correctly reports the chapter as absent and so no confirmation is ever shown.
    /// The recorded file therefore has to still exist inside the title's own folder,
    /// resolved through the same containment rule every other destination uses.
    /// </summary>
    public bool IsPublishedOnDisk(
        DownloadJobIdentity identity,
        string libraryRoot,
        string folderName)
    {
        var record = FindPublished(identity);
        if (record is null) return false;
        if (!DownloaderPathContainment.TryResolve(
                libraryRoot,
                folderName,
                record.FileName,
                out var path,
                out _))
        {
            return false;
        }

        return File.Exists(path);
    }

    /// <summary>
    /// Records a publication. This is the nonessential half of the pair: a
    /// failure here leaves an already published CBZ intact and returns a
    /// repairable warning instead of throwing.
    /// </summary>
    public bool TryRecordPublished(
        DownloadJobIdentity identity,
        string fileName,
        out string? warning)
    {
        ArgumentNullException.ThrowIfNull(identity);
        warning = null;
        try
        {
            lock (_gate)
            {
                var document = ReadForWrite();
                var published = (document.Published ?? []).ToList();
                published.RemoveAll(candidate =>
                    string.Equals(candidate.SourceId, identity.SourceId, StringComparison.Ordinal)
                    && string.Equals(candidate.TitleHid, identity.TitleHid, StringComparison.Ordinal)
                    && string.Equals(candidate.ChapterId, identity.ChapterId, StringComparison.Ordinal)
                    && string.Equals(candidate.GroupId, identity.GroupId, StringComparison.Ordinal));
                published.Add(new PublishedChapterRecord(
                    identity.SourceId,
                    identity.TitleHid,
                    identity.ChapterId,
                    identity.GroupId,
                    fileName,
                    DateTimeOffset.UtcNow));
                document.Published = published;
                DownloaderJson.WriteAtomic(_path, document);
            }

            return true;
        }
        catch (QueuePersistenceException exception)
        {
            warning = exception.Message;
            return false;
        }
    }

    private IndexDocument ReadForWrite()
    {
        var document = DownloaderJson.ReadBounded<IndexDocument>(
            _path,
            MaximumFileBytes,
            out _);
        if (document is null)
        {
            return new IndexDocument { Version = SchemaVersion, Mappings = [], Published = [] };
        }

        if (document.Version != SchemaVersion)
        {
            throw new QueueVersionConflictException(document.Version);
        }

        return document;
    }

    private sealed class IndexDocument
    {
        public int Version { get; set; }

        public List<SourceTitleMapping>? Mappings { get; set; }

        public List<PublishedChapterRecord>? Published { get; set; }
    }
}

public sealed record IndexLoadResult(
    IReadOnlyList<SourceTitleMapping> Mappings,
    IReadOnlyList<PublishedChapterRecord> Published,
    string? Warning,
    int? UnsupportedVersion);
