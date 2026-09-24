namespace Module.Mangareader.Library;

/// <summary>
/// One indexed title. Pure data: no chapter list, no archive access, no WPF.
/// A full startup reads thousands of these from a single small file instead
/// of enumerating every chapter archive on disk.
/// </summary>
public sealed record LibraryIndexEntry(
    string TitleFolderName,
    string FolderPath,
    DateTime AddedUtc,
    int ChapterCount,
    string FirstChapterLabel,
    string LatestChapterLabel,
    DateTime FolderFingerprintUtc,
    string CoverFingerprint,
    string CoverThumbnailPath);

public sealed record LibraryIndexLoadResult(
    string? LibraryRoot,
    DateTime UpdatedUtc,
    IReadOnlyList<LibraryIndexEntry> Entries,
    string? Warning)
{
    public static LibraryIndexLoadResult Empty { get; } = new(null, default, [], null);
}

public sealed record LibraryIndexSaveResult(bool Saved, string? Warning);

/// <summary>
/// Cover thumbnail cache seam. The coordinator calls it whenever an entry is
/// (re)built; a null provider (or a null return) leaves the entry's stored
/// thumbnail path untouched, so caching is best effort and never blocks the
/// index. Production imaging lives in <c>LibraryCoverCache</c> (WPF);
/// tests inject fakes.
/// </summary>
public interface ILibraryCoverThumbnails
{
    string? EnsureThumbnail(LibraryIndexEntry entry, CancellationToken cancellationToken);

    void PurgeExcept(IReadOnlyCollection<string> keepPaths);
}
