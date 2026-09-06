using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Module.Mangareader.Archive;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// One title folder to inspect, plus the optional remote identity the caller is
/// asking about. The probe is neutral: it takes plain provider strings rather
/// than a provider contract type, so neither Library nor Downloader has to hand
/// it a sibling's model.
/// </summary>
public sealed record LocalTitleProbeRequest(
    string RootPath,
    string FolderName,
    string? ProviderId = null,
    string? ProviderTitleId = null)
{
    public bool HasExpectedProvider =>
        !string.IsNullOrWhiteSpace(ProviderId) && !string.IsNullOrWhiteSpace(ProviderTitleId);
}

/// <summary>
/// One archive actually present in the title folder, with the Citadel source
/// identity embedded in it when the archive carries one. A chapter without
/// embedded provenance is still a present chapter; it simply cannot be matched
/// to a provider chapter by identity.
/// </summary>
public sealed record LocalChapterIdentity(
    string FileName,
    string FilePath,
    string? ProviderId,
    string? ProviderTitleId,
    string? ProviderChapterId,
    string? ProviderChapterNumber,
    string? ProviderGroupId,
    string? ProviderGroupName)
{
    public bool HasEmbeddedSource =>
        !string.IsNullOrWhiteSpace(ProviderId)
        && !string.IsNullOrWhiteSpace(ProviderChapterId)
        && !string.IsNullOrWhiteSpace(ProviderGroupId);

    /// <summary>
    /// Chapter number is display metadata, never identity: two groups publish
    /// the same number as distinct variants, so a match requires the provider
    /// chapter key and its group.
    /// </summary>
    public bool IsSameChapter(string? providerId, string? chapterId, string? groupId) =>
        HasEmbeddedSource
        && string.Equals(ProviderId, providerId, StringComparison.Ordinal)
        && string.Equals(ProviderChapterId, chapterId, StringComparison.Ordinal)
        && string.Equals(ProviderGroupId, groupId, StringComparison.Ordinal);

    public bool BelongsToTitle(string? providerId, string? providerTitleId) =>
        !string.IsNullOrWhiteSpace(ProviderId)
        && string.Equals(ProviderId, providerId, StringComparison.Ordinal)
        && string.Equals(ProviderTitleId, providerTitleId, StringComparison.Ordinal);
}

/// <summary>
/// One probe verdict. <see cref="TitleExists"/> means the folder holds a title the
/// way Library defines one — the directory is present and contains at least one
/// supported chapter. An empty folder is therefore absent, which is what lets a
/// cover-only destination be written instead of being reported as an existing
/// title.
/// </summary>
public sealed record LocalTitleProbeResult(
    string TitleFolderPath,
    bool TitleExists,
    IReadOnlyList<LocalChapterIdentity> Chapters,
    IReadOnlyList<string> Warnings)
{
    public int ChapterCount => Chapters.Count;
}

/// <summary>
/// Reads what is actually on disk for one title folder. The filesystem is the
/// only authority here: an index, a Library snapshot or a previous scan may be a
/// hint to a caller, but none of them can make an absent file present or hide a
/// file that was just published.
///
/// The probe is read-only and bounded. It opens only the selected title
/// directory, never enumerates the Library root, and never creates, renames or
/// deletes anything.
/// </summary>
public sealed class LocalTitleProbe
{
    /// <summary>
    /// The provenance entry the Downloader's CBZ publisher writes into every
    /// published archive. Declared here because a module-local reader must not
    /// depend on a feature; the round-trip test in the Downloader suite is what
    /// keeps the two names from drifting.
    /// </summary>
    public const string SourceManifestEntryName = "META-INF/citadel-source.json";

    private static readonly HashSet<string> ChapterExtensions = new(
        [".cbz", ".cbr", ".rar"],
        StringComparer.OrdinalIgnoreCase);

    private readonly ArchiveSignatureDetector _detector = new();

    public Task<LocalTitleProbeResult> ProbeTitleAsync(
        LocalTitleProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => ProbeTitle(request), cancellationToken);
    }

    public LocalTitleProbeResult ProbeTitle(LocalTitleProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FolderName);

        var folder = Path.Combine(request.RootPath, request.FolderName);
        if (!Directory.Exists(folder))
        {
            return new LocalTitleProbeResult(folder, TitleExists: false, [], []);
        }

        var warnings = new List<string>();
        var chapters = new List<LocalChapterIdentity>();

        foreach (var path in Directory
                     .GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => ChapterExtensions.Contains(Path.GetExtension(path)))
                     .OrderBy(path => Path.GetFileName(path), NaturalStringComparer.OrdinalIgnoreCase))
        {
            var identity = ReadChapter(path, request, warnings);
            if (identity is not null) chapters.Add(identity);
        }

        return new LocalTitleProbeResult(folder, TitleExists: chapters.Count > 0, chapters, warnings);
    }

    /// <summary>
    /// Recognizes one candidate chapter from its content signature, then reads
    /// its embedded Citadel identity when the container can carry one. Any
    /// failure to read is a warning, never a thrown probe: an archive locked by
    /// the Reader still counts as present.
    /// </summary>
    private LocalChapterIdentity? ReadChapter(
        string path,
        LocalTitleProbeRequest request,
        List<string> warnings)
    {
        var fileName = Path.GetFileName(path);

        ArchiveFormat format;
        try
        {
            format = _detector.Detect(path).Format;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{fileName}: tidak dapat dibaca ({exception.GetBaseException().Message}).");
            return null;
        }

        if (format is not (ArchiveFormat.Zip or ArchiveFormat.Rar4 or ArchiveFormat.Rar5))
        {
            warnings.Add($"{fileName}: bukan arsip chapter yang didukung.");
            return null;
        }

        var manifest = format == ArchiveFormat.Zip ? ReadSourceManifest(path, fileName, warnings) : null;
        if (manifest is null && format != ArchiveFormat.Zip)
        {
            warnings.Add($"{fileName}: identitas sumber Citadel tidak terbaca dari arsip non-ZIP.");
        }

        var identity = new LocalChapterIdentity(
            fileName,
            path,
            manifest?.Provider,
            manifest?.TitleId,
            manifest?.ChapterId,
            manifest?.ChapterNumber,
            manifest?.GroupId,
            manifest?.GroupName);

        if (request.HasExpectedProvider
            && identity.HasEmbeddedSource
            && !identity.BelongsToTitle(request.ProviderId, request.ProviderTitleId))
        {
            warnings.Add(
                $"{fileName}: provenance tertanam adalah {identity.ProviderId}/{identity.ProviderTitleId}, bukan title yang diminta.");
        }

        return identity;
    }

    private static EmbeddedSourceManifest? ReadSourceManifest(
        string path,
        string fileName,
        List<string> warnings)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            using var entry = archive.GetEntry(SourceManifestEntryName)?.Open();
            if (entry is null) return null;
            return JsonSerializer.Deserialize<EmbeddedSourceManifest>(entry);
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or NotSupportedException)
        {
            warnings.Add($"{fileName}: provenance tertanam tidak dapat dibaca.");
            return null;
        }
    }

    /// <summary>
    /// The embedded provenance shape as the publisher writes it. Read-only copy:
    /// this reader never writes provenance and never imports the writer.
    /// </summary>
    private sealed record EmbeddedSourceManifest
    {
        public string? Provider { get; init; }

        public string? TitleId { get; init; }

        public string? ChapterId { get; init; }

        public string? ChapterNumber { get; init; }

        public string? GroupId { get; init; }

        public string? GroupName { get; init; }
    }
}
