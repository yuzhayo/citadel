using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Citadel.Setting.Components;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Library;
using Module.Mangareader.Library.UpdateChecker;
using Module.Mangareader.Sources;

namespace Module.Mangareader;

/// <summary>
/// The module's cross-feature coordination rules, each with one named home.
///
/// These five rules each span two or more features (CatalogMirror, Downloader,
/// Library, and the shared UI), which is why FM-5 places them at the composition
/// root rather than inside any single feature: a rule that touches three owners
/// belongs to none of them. They live in this file, separate from the view's
/// composition and lifecycle, so one file holds composition and one file holds
/// coordination. Every dependency is an explicit parameter; nothing is reached
/// for through statics or other features' internals.
/// </summary>
public static class MangaReaderHandoffs
{
    /// <summary>
    /// Routes one confirmed folder mapping from the Downloader's index into the
    /// neutral shape Library's Update Checker reads. Translation only — the root
    /// never decides which mapping is correct.
    /// </summary>
    public static ConfirmedTitleMapping? ReadConfirmedMapping(
        DownloadSourceIndex index,
        string folderName)
    {
        var mapping = index.Load().Mappings.FirstOrDefault(candidate =>
            string.Equals(candidate.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        return mapping is null
            ? null
            : new ConfirmedTitleMapping(
                mapping.SourceId,
                mapping.TitleId,
                mapping.TitleHid,
                mapping.FolderName);
    }

    /// <summary>
    /// Routes one immutable update selection to the existing queue command and
    /// reports the queue's own answer. A queue that cannot be written down must
    /// not accept the job, so that failure is reported rather than swallowed.
    /// </summary>
    public static UpdateHandoffResult EnqueueUpdate(
        UpdateDownloadRequest request,
        DownloadQueueFeature queue)
    {
        try
        {
            var result = queue.QueueChapters(
                new RemoteTitleSummary(
                    request.Title,
                    request.TitleDisplayName,
                    CoverUrl: null,
                    LatestChapterLabel: null),
                new RemoteSourceGroup(request.Group, request.GroupDisplayName),
                request.Chapters,
                request.LocalFolderName);
            // The neutral handoff carries one skip total. Both of the queue's skip
            // reasons mean "this chapter was not queued", so the translation adds them
            // rather than dropping one on the floor.
            return new UpdateHandoffResult(
                result.Queued,
                result.SkippedAlreadyPublished + result.SkippedAlreadyQueued,
                result.Blocked);
        }
        catch (QueuePersistenceException exception)
        {
            return new UpdateHandoffResult(
                0,
                0,
                "Queue tidak dapat disimpan: " + exception.Message);
        }
    }

    /// <summary>
    /// Answers CatalogMirror's "which of these chapters already exist locally" by
    /// probing through the Downloader's Lister, then translating the probe into
    /// CatalogMirror's own availability shape. The verdict rule (what counts as
    /// locally available) stays with Lister; this rule only bridges the two
    /// contracts.
    /// </summary>
    public static async Task<CatalogLocalAvailabilityResult> CheckCatalogAvailability(
        ListerFeature lister,
        CatalogLocalAvailabilityRequest request,
        CancellationToken token)
    {
        var summary = new RemoteTitleSummary(
            new RemoteTitleIdentity(
                request.SourceId, request.TitleId, request.TitleHid, Slug: string.Empty),
            request.TitleDisplayName,
            CoverUrl: null,
            LatestChapterLabel: null);
        var listed = await lister.ListAsync(summary, token).ConfigureAwait(false);
        return new CatalogLocalAvailabilityResult(request.Chapters.Select(chapter =>
            new CatalogChapterAvailability(
                chapter.ChapterId,
                ListerFeature.IsLocallyAvailable(
                    listed,
                    new RemoteChapterIdentity(
                        request.SourceId,
                        summary.Identity,
                        chapter.ChapterId,
                        chapter.ChapterNumber,
                        new RemoteGroupIdentity(request.SourceId, request.GroupId))))).ToArray());
    }

    /// <summary>
    /// Resolves the destination folder for a Catalog queue request: an existing
    /// confirmed mapping wins; otherwise the deterministic suggestion wins unless
    /// the folder already exists for another title, in which case the user is
    /// asked, and a declined claim falls back to a hid-suffixed folder. The
    /// folder-collision policy spans Library's root, Downloader's index and the
    /// shared confirm dialog, so it lives here rather than in any one of them.
    /// Returns null when no usable folder exists.
    /// </summary>
    public static string? ConfirmQueueTarget(
        LibraryRootContext libraryRoot,
        DownloadSourceIndex index,
        Func<Window?> ownerWindow,
        CatalogQueueTargetRequest request)
    {
        var root = libraryRoot.CurrentRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var identity = new RemoteTitleIdentity(
            request.SourceId, request.TitleId, request.TitleHid, Slug: string.Empty);
        var existing = index.FindUsableMapping(root, identity);
        if (existing is not null) return existing.FolderName;

        var suggested = DownloadQueueFeature.SanitizeFolder(request.TitleDisplayName);
        if (!Directory.Exists(Path.Combine(root, suggested))) return suggested;

        var claimed = SettingDialog.Confirm(
            ownerWindow(),
            "Catalog",
            $"Folder '{suggested}' sudah ada di Library tetapi tidak dipetakan ke title ini.\n\nGunakan folder itu untuk '{request.TitleDisplayName}'?",
            "Use folder");
        if (claimed) return suggested;

        var distinct = $"{suggested} [{request.TitleHid}]";
        return Directory.Exists(Path.Combine(root, distinct)) ? null : distinct;
    }

    /// <summary>
    /// Translates one immutable Catalog handoff into the existing queue call.
    /// The queue's own answer is returned as-is: dedupe, persistence and collision
    /// rules are never reinterpreted here. Returns null on success, or the
    /// persistence failure message when the queue could not be written down.
    /// </summary>
    public static string? QueueHandoff(
        CatalogQueueHandoffRequest handoff,
        DownloadQueueFeature queue)
    {
        try
        {
            var title = new RemoteTitleSummary(
                new RemoteTitleIdentity(
                    handoff.SourceId, handoff.TitleId, handoff.TitleHid, Slug: string.Empty),
                handoff.TitleDisplayName,
                CoverUrl: null,
                LatestChapterLabel: null);
            var group = new RemoteSourceGroup(
                new RemoteGroupIdentity(handoff.SourceId, handoff.GroupId),
                handoff.GroupDisplayName);
            // OrderIndex is the provider list order, preserved through the
            // selection: the handoff chapters arrive in state order.
            var chapters = handoff.Chapters.Select((candidate, order) => new RemoteChapterSummary(
                new RemoteChapterIdentity(
                    handoff.SourceId,
                    title.Identity,
                    candidate.ChapterId,
                    candidate.ChapterNumber,
                    group.Identity),
                candidate.DisplayName,
                OrderIndex: order)).ToArray();
            queue.QueueChapters(title, group, chapters, handoff.TargetFolder);
            return null;
        }
        catch (QueuePersistenceException exception)
        {
            return exception.Message;
        }
    }
}
