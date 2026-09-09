using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

// Explicit title detail, group, chapter and selection flow. It resolves the
// source per title, loads detail, groups and the selected group's chapters,
// asks the injected availability delegate for the selected title/group only,
// saves detail enrichment, fetches at most one cover when no valid cache
// exists, and builds the immutable queue-handoff request. It never syncs the
// catalog, reads the Library snapshot, imports ListerFeature, writes the
// queue, or renders controls. All public async methods are safe to call from
// the UI thread.
public sealed class CatalogMirrorDetailFeature(
    IMangaSourceDirectory sources,
    CatalogEnrichmentStore enrichment,
    CatalogMirrorCoverCache covers,
    Func<CatalogLocalAvailabilityRequest, CancellationToken, Task<CatalogLocalAvailabilityResult>> checkAvailability)
{
    private readonly IMangaSourceDirectory _sources = sources;
    private readonly CatalogEnrichmentStore _enrichment = enrichment;
    private readonly CatalogMirrorCoverCache _covers = covers;
    private readonly Func<CatalogLocalAvailabilityRequest, CancellationToken, Task<CatalogLocalAvailabilityResult>> _checkAvailability =
        checkAvailability;

    private readonly object _gate = new();
    private CatalogDetailState? _current;
    private RemoteTitleDetail? _detail;
    private IReadOnlyList<RemoteChapterSummary> _chapters = [];
    private int _generation;
    private bool _disposed;

    public CatalogDetailState? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<CatalogDetailState?>? StateChanged;

    /// <summary>
    /// Opens one title: detail, groups and the first provider group's
    /// chapters load online. Nothing here runs before this call.
    /// </summary>
    public async Task OpenTitleAsync(CatalogSnapshotItem title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);

        var generation = NextGeneration();
        Publish(new CatalogDetailState(title, null, [], string.Empty, [], IsLoading: true, ErrorMessage: null));

        var source = _sources.FindSource(title.SourceId);
        if (source is null)
        {
            Publish(new CatalogDetailState(
                title, null, [], string.Empty, [], IsLoading: false,
                $"Source '{title.SourceId}' is not available."));
            return;
        }

        RemoteTitleDetail detail;
        IReadOnlyList<RemoteSourceGroup> groups;
        try
        {
            detail = await source
                .GetTitleAsync(IdentityOf(title), cancellationToken)
                .ConfigureAwait(false);
            if (!IsCurrent(generation)) return;
            groups = await source
                .GetGroupsAsync(IdentityOf(title), cancellationToken)
                .ConfigureAwait(false);
            if (!IsCurrent(generation)) return;
        }
        catch (Exception exception)
        {
            Publish(new CatalogDetailState(
                title, null, [], string.Empty, [], IsLoading: false,
                exception.GetBaseException().Message));
            return;
        }

        lock (_gate)
        {
            _detail = detail;
        }

        await SaveEnrichmentAsync(title, detail).ConfigureAwait(false);

        if (groups.Count == 0)
        {
            if (!IsCurrent(generation)) return;
            Publish(new CatalogDetailState(
                title, DisplayOf(detail, null), [], string.Empty, [], IsLoading: false,
                "No source groups."));
            return;
        }

        await LoadGroupChaptersAsync(title, detail, source, groups, groups[0].Identity.GroupId, generation, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Switches the active group: clears chapters and selection first, then
    /// loads the new group's chapters.
    /// </summary>
    public async Task SelectGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        CatalogDetailState? current;
        RemoteTitleDetail? detail;
        IMangaSource? source;
        lock (_gate)
        {
            current = _current;
            detail = _detail;
        }

        if (current is null || detail is null)
        {
            return;
        }

        if (string.Equals(current.SelectedGroupId, groupId, StringComparison.Ordinal))
        {
            return;
        }

        source = _sources.FindSource(current.Title.SourceId);
        if (source is null)
        {
            return;
        }

        var generation = NextGeneration();
        var groups = current.Groups
            .Select(group => new RemoteSourceGroup(
                new RemoteGroupIdentity(current.Title.SourceId, group.GroupId), group.DisplayName))
            .ToArray();
        Publish(current with
        {
            Chapters = [],
            IsLoading = true,
            ErrorMessage = null,
        });

        await LoadGroupChaptersAsync(
            current.Title, detail, source, groups, groupId, generation, cancellationToken)
            .ConfigureAwait(false);
    }

    public void SetChapterSelected(string chapterId, bool selected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterId);
        lock (_gate)
        {
            if (_disposed || _current is null || _current.IsLoading) return;
            Publish(_current with
            {
                Chapters = _current.Chapters
                    .Select(chapter => string.Equals(chapter.ChapterId, chapterId, StringComparison.Ordinal)
                        ? chapter with { IsSelected = selected }
                        : chapter)
                    .ToArray(),
            });
        }
    }

    public void SelectAllChapters(bool selected)
    {
        lock (_gate)
        {
            if (_disposed || _current is null || _current.IsLoading) return;
            Publish(_current with
            {
                Chapters = _current.Chapters
                    .Select(chapter => chapter with { IsSelected = selected })
                    .ToArray(),
            });
        }
    }

    /// <summary>Closes the detail; the view restores the exact local result.</summary>
    public void Close()
    {
        NextGeneration();
        lock (_gate)
        {
            _detail = null;
            _chapters = [];
        }

        Publish(null);
    }

    /// <summary>
    /// Builds the immutable handoff for the current selection, or null without
    /// a selection, while loading, on error, or without a confirmed folder.
    /// </summary>
    public CatalogQueueHandoffRequest? TryBuildHandoff(string targetFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder)) return null;

        CatalogDetailState? current;
        RemoteTitleDetail? detail;
        lock (_gate)
        {
            current = _current;
            detail = _detail;
        }

        if (current is null || detail is null || current.IsLoading || current.ErrorMessage is not null)
        {
            return null;
        }

        var selected = current.Chapters.Where(chapter => chapter.IsSelected).ToArray();
        if (selected.Length == 0) return null;

        var group = current.Groups.FirstOrDefault(candidate =>
            string.Equals(candidate.GroupId, current.SelectedGroupId, StringComparison.Ordinal));
        var numbers = new Dictionary<string, RemoteChapterSummary>(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var position = 0; position < _chapters.Count; position++)
        {
            // First wins on duplicate provider ids, so a repeated chapter can
            // never crash the handoff; the ordinal is the provider list order.
            numbers.TryAdd(_chapters[position].Identity.ChapterId, _chapters[position]);
            order.TryAdd(_chapters[position].Identity.ChapterId, position);
        }

        return new CatalogQueueHandoffRequest(
            current.Title.SourceId,
            current.Title.TitleId,
            current.Title.TitleHid,
            detail.Summary.DisplayName,
            current.SelectedGroupId,
            group?.DisplayName ?? current.SelectedGroupId,
            selected.Select(chapter => new CatalogChapterSelection(
                chapter.ChapterId,
                numbers.TryGetValue(chapter.ChapterId, out var summary)
                    ? summary.Identity.ChapterNumber
                    : chapter.DisplayName,
                chapter.DisplayName)).ToArray(),
            targetFolder);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private async Task LoadGroupChaptersAsync(
        CatalogSnapshotItem title,
        RemoteTitleDetail detail,
        IMangaSource source,
        IReadOnlyList<RemoteSourceGroup> groups,
        string groupId,
        int generation,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RemoteChapterSummary> chapters;
        try
        {
            chapters = await source.GetChaptersAsync(
                IdentityOf(title),
                new RemoteGroupIdentity(title.SourceId, groupId),
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation)) return;
        }
        catch (Exception exception)
        {
            if (!IsCurrent(generation)) return;
            Publish(new CatalogDetailState(
                title, DisplayOf(detail, null), MapGroups(groups), groupId, [],
                IsLoading: false, exception.GetBaseException().Message));
            return;
        }

        lock (_gate)
        {
            _chapters = chapters;
        }

        var available = await CheckAvailabilityAsync(
                title, detail.Summary.DisplayName, groupId, chapters, cancellationToken)
            .ConfigureAwait(false);
        if (!IsCurrent(generation)) return;

        string? coverPath = _covers.TryGetCachedPath(title.SourceId, title.TitleId);
        if (coverPath is null)
        {
            try
            {
                coverPath = await _covers.GetCoverPathAsync(
                    title.SourceId, title.TitleId, detail.Summary.CoverUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A failed cover never fails the detail.
                coverPath = null;
            }

            if (!IsCurrent(generation)) return;
        }

        Publish(new CatalogDetailState(
            title,
            DisplayOf(detail, coverPath),
            MapGroups(groups),
            groupId,
            chapters.Select(chapter => new CatalogDetailChapter(
                chapter.Identity.ChapterId,
                chapter.DisplayName,
                available.TryGetValue(chapter.Identity.ChapterId, out var present) && present,
                IsSelected: false)).ToArray(),
            IsLoading: false,
            ErrorMessage: null));
    }

    private async Task<Dictionary<string, bool>> CheckAvailabilityAsync(
        CatalogSnapshotItem title,
        string titleDisplayName,
        string groupId,
        IReadOnlyList<RemoteChapterSummary> chapters,
        CancellationToken cancellationToken)
    {
        var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            var result = await _checkAvailability(
                new CatalogLocalAvailabilityRequest(
                    title.SourceId,
                    title.TitleId,
                    title.TitleHid,
                    titleDisplayName,
                    groupId,
                    chapters.Select(chapter => new CatalogAvailabilityChapter(
                        chapter.Identity.ChapterId, chapter.Identity.ChapterNumber)).ToArray()),
                cancellationToken).ConfigureAwait(false);
            foreach (var chapter in result.Chapters)
            {
                verdicts[chapter.ChapterId] = chapter.Available;
            }
        }
        catch (Exception)
        {
            // Availability is advisory: every chapter simply reads as remote.
        }

        return verdicts;
    }

    private async Task SaveEnrichmentAsync(CatalogSnapshotItem title, RemoteTitleDetail detail)
    {
        try
        {
            await _enrichment.WriteAsync(
                new CatalogTitleEnrichment(
                    title.SourceId,
                    title.TitleId,
                    title.TitleHid,
                    detail.Summary.DisplayName,
                    detail.Description,
                    DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Enrichment is a cache: losing one write never fails the detail.
        }
    }

    private static CatalogDetailDisplay DisplayOf(RemoteTitleDetail detail, string? coverPath) =>
        new(detail.Summary.DisplayName,
            detail.Description,
            detail.Genres.Count == 0
                ? null
                : string.Join(", ", detail.Genres.Select(genre => genre.DisplayName)),
            detail.Metadata,
            coverPath);

    private static IReadOnlyList<CatalogDetailGroup> MapGroups(
        IReadOnlyList<RemoteSourceGroup> groups) =>
        groups.Select(group => new CatalogDetailGroup(group.Identity.GroupId, group.DisplayName)).ToArray();

    private static RemoteTitleIdentity IdentityOf(CatalogSnapshotItem title) =>
        new(title.SourceId, title.TitleId, title.TitleHid, Slug: string.Empty);

    private int NextGeneration() => Interlocked.Increment(ref _generation);

    private bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

    private void Publish(CatalogDetailState? state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _current = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
