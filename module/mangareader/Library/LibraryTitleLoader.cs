using System.IO;
using Module.Mangareader.Archive;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library;

/// <summary>
/// Single-title filesystem reads for the Library: chapter enumeration,
/// full title load, and index entry builds. One folder in, no library-wide
/// scan out — the unit the lazy Library is built from. Every consumer
/// (grid open, History resolve, Cover Builder resolve, reconciliation)
/// shares this one definition of what a title contains.
/// </summary>
public sealed class LibraryTitleLoader
{
    private static readonly HashSet<string> ChapterExtensions = new(
        [".cbz", ".cbr", ".rar"],
        StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, bool> _isSupportedArchive;

    public LibraryTitleLoader(Func<string, bool>? isSupportedArchive = null)
    {
        _isSupportedArchive = isSupportedArchive ?? new ArchivePageReader().IsSupportedArchive;
    }

    /// <summary>
    /// Chapter files of one title folder, natural-sorted by file name.
    /// Filename metadata only — no archive is opened.
    /// </summary>
    public IReadOnlyList<ChapterInfo> ListChapters(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        return Directory
            .GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ChapterExtensions.Contains(Path.GetExtension(path)))
            .Where(_isSupportedArchive)
            .OrderBy(
                path => Path.GetFileName(path) ?? string.Empty,
                NaturalStringComparer.OrdinalIgnoreCase)
            .Select(path => new ChapterInfo(Path.GetFileNameWithoutExtension(path), path))
            .ToArray();
    }

    /// <summary>
    /// Full title for one folder, or null when the folder is gone or holds
    /// no chapters. Null (not an exception) is the lazy-open contract: a
    /// title that vanished since indexing fails per-title, never per-library.
    /// </summary>
    public MangaTitle? LoadTitle(
        string folderPath,
        DateTime? addedUtcOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(folderPath.Trim());
        if (!Directory.Exists(fullPath)) return null;

        var chapters = ListChapters(fullPath, cancellationToken);
        if (chapters.Count == 0) return null;

        return new MangaTitle(
            Path.GetFileName(fullPath) ?? string.Empty,
            fullPath,
            chapters)
        {
            // A title enters this folder-based Library when its directory is
            // created. Unlike LastWriteTime, this does not move every time a
            // chapter is downloaded into the title folder.
            AddedUtc = addedUtcOverride ?? Directory.GetCreationTimeUtc(fullPath),
        };
    }

    /// <summary>
    /// Index entry for one folder, or null when there is nothing to index.
    /// A reindex never invents a new <c>AddedUtc</c>: pass the stored entry
    /// and its timestamp survives (R-01). New titles fall back to folder
    /// creation time, the same rule the full scan always used.
    /// Cover thumbnail bytes are not produced here — the entry carries the
    /// deterministic cover <em>selection</em> (R-02) and the thumbnail cache
    /// path stays empty until the cover increment fills it.
    /// </summary>
    public LibraryIndexEntry? BuildEntry(
        string folderPath,
        LibraryIndexEntry? existing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(folderPath.Trim());
        if (!Directory.Exists(fullPath)) return null;

        var chapters = ListChapters(fullPath, cancellationToken);
        if (chapters.Count == 0) return null;

        return new LibraryIndexEntry(
            Path.GetFileName(fullPath) ?? string.Empty,
            fullPath,
            existing?.AddedUtc ?? Directory.GetCreationTimeUtc(fullPath),
            chapters.Count,
            chapters[0].Title,
            chapters[^1].Title,
            Directory.GetLastWriteTimeUtc(fullPath),
            CoverFingerprint(fullPath, chapters[0].FilePath),
            string.Empty);
    }

    /// <summary>
    /// Deterministic cover source for a title (R-02): a `cover.*` file wins
    /// by the shared <see cref="TitleCoverFiles"/> rule, otherwise the
    /// natural-first archive. Same folder state always yields the same
    /// string, so equal fingerprints mean "cover unchanged" without opening
    /// anything.
    /// </summary>
    internal static string CoverFingerprint(string folderPath, string firstArchivePath)
    {
        var cover = TitleCoverFiles.Enumerate(folderPath).FirstOrDefault();
        if (cover is not null) return CoverKey("cover", cover);

        return CoverKey("archive", firstArchivePath);
    }

    private static string CoverKey(string kind, string path)
    {
        DateTime writeUtc;
        try
        {
            writeUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            writeUtc = DateTime.MinValue;
        }

        return $"{kind}:{Path.GetFileName(path)}#{writeUtc.Ticks}";
    }
}
