using System.IO;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Lister;

/// <summary>
/// What local storage currently holds for one remote title: whether its
/// deterministic folder exists, which chapters and source variants are actually
/// in it, and any non-fatal read warning.
/// </summary>
public sealed record ListerResult(
    string LocalFolderName,
    bool TitleExists,
    IReadOnlyList<LocalChapterIdentity> Chapters,
    IReadOnlyList<string> Warnings)
{
    public static ListerResult Absent(string localFolderName) =>
        new(localFolderName, TitleExists: false, [], []);

    public int ChapterCount => Chapters.Count;
}

/// <summary>
/// Answers local availability for the Catalog detail from the filesystem, not
/// from a Library snapshot. Library refresh is deliberately not chained to
/// download completion, so a scan result can be stale in both directions: a file
/// published since the last scan must already count as present, and a file the
/// user deleted must already count as missing.
///
/// Lister is read-only and bounded. It runs only when the detail opens, when the
/// source group changes, or when the user returns to the detail — never on a
/// timer — and it probes only the selected title folder, never the whole Library.
/// It never queues, downloads, fetches a cover or mutates Library state.
/// </summary>
public sealed class ListerFeature
{
    private readonly LocalTitleProbe _probe;
    private readonly Func<string?> _libraryRoot;
    private readonly Func<string, RemoteTitleSummary, string> _resolveFolder;

    public ListerFeature(
        LocalTitleProbe probe,
        Func<string?> libraryRoot,
        Func<string, RemoteTitleSummary, string> resolveFolder)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _libraryRoot = libraryRoot ?? throw new ArgumentNullException(nameof(libraryRoot));
        _resolveFolder = resolveFolder ?? throw new ArgumentNullException(nameof(resolveFolder));
    }

    public async Task<ListerResult> ListAsync(
        RemoteTitleSummary title,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);

        var root = _libraryRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return ListerResult.Absent(string.Empty);
        }

        var folder = _resolveFolder(root, title);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return ListerResult.Absent(string.Empty);
        }

        var probed = await _probe.ProbeTitleAsync(
            new LocalTitleProbeRequest(root, folder, title.Identity.SourceId, title.Identity.TitleId),
            cancellationToken).ConfigureAwait(false);

        return new ListerResult(folder, probed.TitleExists, probed.Chapters, probed.Warnings);
    }

    /// <summary>
    /// Whether one remote chapter already exists in the probed folder. An
    /// embedded Citadel identity is decisive. A local archive carrying embedded
    /// provenance for a different group is a distinct variant and never counts as
    /// this chapter, even at the same number; only a local archive with no
    /// embedded provenance falls back to its chapter number, so an older library
    /// is not told to re-download what it already has.
    /// </summary>
    public static bool IsLocallyAvailable(ListerResult result, RemoteChapterIdentity chapter)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(chapter);

        foreach (var local in result.Chapters)
        {
            if (local.IsSameChapter(chapter.SourceId, chapter.ChapterId, chapter.Group.GroupId))
            {
                return true;
            }

            if (local.HasEmbeddedSource) continue;

            if (ChapterNumberText.Equivalent(
                    Path.GetFileNameWithoutExtension(local.FileName),
                    chapter.ChapterNumber))
            {
                return true;
            }
        }

        return false;
    }
}
