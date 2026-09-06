using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Features.Downloader.Catalog;

/// <summary>
/// One immutable Catalog snapshot. The screen renders it; it never mutates it.
/// </summary>
public sealed record CatalogState
{
    public string? SelectedSourceId { get; init; }

    public string? SelectedSourceName { get; init; }

    /// <summary>Whether a Browse or its Load more is in flight.</summary>
    public bool IsBrowseBusy { get; init; }

    /// <summary>Whether a detail action — open title, change group — is in flight.</summary>
    public bool IsActionBusy { get; init; }

    /// <summary>
    /// The union, and what the presentation disables input on. Keeping the two halves
    /// apart is what lets a Stop end only the Browse: with one shared flag, stopping a
    /// Browse reported the whole screen idle while a detail was still being fetched,
    /// which re-enabled the provider picker and the search field mid-action.
    /// </summary>
    public bool IsBusy => IsBrowseBusy || IsActionBusy;

    public bool IsDetailOpen { get; init; }

    public string? StatusMessage { get; init; }

    /// <summary>
    /// A non-destructive error. The last successful result set stays visible
    /// beside it so a failed request never blanks the screen.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public IReadOnlyList<RemoteTitleSummary> Results { get; init; } = [];

    /// <summary>Null when the provider did not report a count.</summary>
    public long? TotalCount { get; init; }

    public int Page { get; init; }

    public bool CanLoadMore { get; init; }

    public RemoteTitleDetail? Detail { get; init; }

    public IReadOnlyList<RemoteSourceGroup> Groups { get; init; } = [];

    public RemoteGroupIdentity? SelectedGroup { get; init; }

    public IReadOnlyList<RemoteChapterSummary> Chapters { get; init; } = [];

    public IReadOnlyList<string> SelectedChapterIds { get; init; } = [];

    /// <summary>Restored on Back so the grid returns to its exact anchor.</summary>
    public double ResultsScrollOffset { get; init; }

    public bool HasLibraryRoot { get; init; }

    public bool HasSelection => SelectedChapterIds.Count > 0;
}

/// <summary>
/// Owns Catalog's query, result, detail and selection state, and the
/// latest-request-wins guard. UI-free: the screen translates input into these
/// calls and renders <see cref="CatalogState"/>.
///
/// Nothing here issues a request unless an explicit action asks for one.
/// Selecting a provider, editing a filter or typing search text changes state
/// only.
/// </summary>
public sealed class CatalogFeature
{
    private readonly MangaSourceRegistry _sources;
    private readonly Func<bool> _hasLibraryRoot;
    private readonly object _gate = new();

    private IRemoteFilterContribution? _filters;
    private RemoteBrowseRequest? _activeRequest;

    /// <summary>
    /// Two independent staleness counters. Browse and its Load more share one,
    /// because a later page request really does supersede the earlier one; detail
    /// actions share the other. Keeping them apart is what lets a Stop invalidate
    /// only the Browse it stops — one shared counter made abandoning a Browse throw
    /// away an unrelated title or group result that was still in flight.
    /// </summary>
    private int _browseGeneration;
    private int _actionGeneration;
    private CatalogState _state = new();

    public CatalogFeature(MangaSourceRegistry sources, Func<bool> hasLibraryRoot)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _hasLibraryRoot = hasLibraryRoot ?? throw new ArgumentNullException(nameof(hasLibraryRoot));
        _state = _state with { HasLibraryRoot = hasLibraryRoot() };
    }

    public event EventHandler? StateChanged;

    public CatalogState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public IReadOnlyList<MangaSourceRegistration> AvailableSources => _sources.Sources;

    public IRemoteFilterContribution? Filters
    {
        get
        {
            lock (_gate)
            {
                return _filters;
            }
        }
    }

    /// <summary>
    /// Selecting a provider performs no remote request and starts no browser.
    /// It only swaps the filter contribution that owns this provider's inputs.
    /// </summary>
    public void SelectSource(string sourceId)
    {
        var registration = _sources.Find(sourceId);

        // Latest navigation wins, in both directions: an old Browse may not paint the
        // previous provider's results into this screen, and an old detail may not
        // re-open over it. Both busy halves are released for the same reason — the
        // operations they tracked are now stale and return early without touching
        // state, so nothing else would ever clear those flags.
        NextBrowseGeneration();
        NextActionGeneration();

        Mutate(state =>
        {
            if (registration is null)
            {
                return state with
                {
                    SelectedSourceId = null,
                    SelectedSourceName = null,
                    IsBrowseBusy = false,
                    IsActionBusy = false,
                    ErrorMessage = $"Source '{sourceId}' tidak terdaftar.",
                };
            }

            _filters = registration.CreateFilters();
            return state with
            {
                SelectedSourceId = registration.Id,
                SelectedSourceName = registration.DisplayName,
                // A provider change invalidates the previous provider's results.
                Results = [],
                TotalCount = null,
                Page = 0,
                CanLoadMore = false,
                IsDetailOpen = false,
                Detail = null,
                Groups = [],
                SelectedGroup = null,
                Chapters = [],
                SelectedChapterIds = [],
                IsBrowseBusy = false,
                IsActionBusy = false,
                ErrorMessage = null,
                StatusMessage = null,
                HasLibraryRoot = _hasLibraryRoot(),
            };
        });
        _activeRequest = null;
    }

    /// <summary>Records the grid anchor before opening detail, for Back.</summary>
    public void RememberScrollOffset(double offset) =>
        Mutate(state => state with { ResultsScrollOffset = offset });

    /// <summary>
    /// Snapshots the current query and replaces the prior result set. A later
    /// Start wins: an older response finishes its cleanup but cannot replace
    /// results, count, error or pagination.
    /// </summary>
    public async Task StartAsync(string? query, CancellationToken cancellationToken)
    {
        var source = CurrentSource();
        if (source is null) return;

        var filterState = _filters?.State;
        if (filterState?.HasBlockingError == true)
        {
            Mutate(state => state with
            {
                ErrorMessage = filterState.ValidationMessage,
                IsBrowseBusy = false,
            });
            return;
        }

        var request = new RemoteBrowseRequest(
            string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Page: 1,
            filterState?.CurrentFilter);
        _activeRequest = request;
        var generation = NextBrowseGeneration();

        // Latest navigation wins: a detail still in flight from before this Browse is
        // dropped rather than allowed to re-open over the fresh result page. Its busy
        // half is released here because the abandoned action returns early without
        // touching state, so nothing else would ever clear it.
        NextActionGeneration();

        Mutate(state => state with
        {
            IsBrowseBusy = true,
            IsActionBusy = false,
            ErrorMessage = null,
            StatusMessage = "Mengambil katalog…",
            HasLibraryRoot = _hasLibraryRoot(),
        });

        try
        {
            var page = await source.BrowseAsync(request, cancellationToken).ConfigureAwait(false);
            if (IsBrowseStale(generation)) return;

            if (cancellationToken.IsCancellationRequested)
            {
                // Only a superseding Browse or the screen's disposal cancels this
                // token — a logical Stop leaves the transport alone. Either way the
                // bounded inline command may still answer afterwards, and that late
                // response must not replace results, count, error or pagination.
                Mutate(state => state with { IsBrowseBusy = false, StatusMessage = null });
                return;
            }

            Mutate(state => state with
            {
                Results = page.Items,
                TotalCount = page.Total,
                Page = page.Page,
                CanLoadMore = page.HasMore,
                IsDetailOpen = false,
                Detail = null,
                StatusMessage = Describe(page),
                IsBrowseBusy = false,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsBrowseStale(generation))
            {
                Mutate(state => state with { IsBrowseBusy = false, StatusMessage = null });
            }
        }
        catch (Exception exception)
        {
            if (IsBrowseStale(generation)) return;
            Mutate(state => state with
            {
                // The previous successful result set stays visible.
                ErrorMessage = exception.GetBaseException().Message,
                IsBrowseBusy = false,
                StatusMessage = null,
            });
        }
    }

    /// <summary>
    /// Logically abandons the in-flight Browse without touching its transport.
    ///
    /// Cancelling the caller's token would not stop the Python work: the inline
    /// pyhost command links that token to its own bounded timeout, so a cancel
    /// completes the pending call at once with a TIMEOUT while Python is still
    /// busy. That surfaces a fake provider error, returns the bar to a terminal
    /// presentation too early, and lets a following Start be written before the old
    /// command has settled.
    ///
    /// So the request is only marked stale, which is what suppresses its late
    /// result, and the busy state is released. The transport token is cancelled
    /// solely by the screen's disposal — a real end of lifetime, not a Stop.
    ///
    /// Staleness here is the Browse counter alone. An <see cref="OpenTitleAsync"/>
    /// or <see cref="SelectGroupAsync"/> that is still in flight advances the
    /// separate action counter, so stopping a Browse never discards a detail result
    /// the user is waiting for.
    /// </summary>
    public void AbandonActiveBrowse()
    {
        NextBrowseGeneration();
        Mutate(state => state with { IsBrowseBusy = false, StatusMessage = null });
    }

    /// <summary>Appends the next page for the current query snapshot only.</summary>
    public async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        var source = CurrentSource();
        var request = _activeRequest;
        if (source is null || request is null) return;

        var next = request with { Page = request.Page + 1 };
        var generation = NextBrowseGeneration();
        Mutate(state => state with { IsBrowseBusy = true, ErrorMessage = null });

        try
        {
            var page = await source.BrowseAsync(next, cancellationToken).ConfigureAwait(false);
            if (IsBrowseStale(generation)) return;

            if (cancellationToken.IsCancellationRequested)
            {
                Mutate(state => state with { IsBrowseBusy = false });
                return;
            }

            _activeRequest = next;
            Mutate(state =>
            {
                var combined = new List<RemoteTitleSummary>(state.Results);
                var known = new HashSet<string>(
                    state.Results.Select(item => item.Identity.TitleId),
                    StringComparer.Ordinal);
                foreach (var item in page.Items)
                {
                    if (known.Add(item.Identity.TitleId)) combined.Add(item);
                }

                return state with
                {
                    Results = combined,
                    TotalCount = page.Total ?? state.TotalCount,
                    Page = next.Page,
                    CanLoadMore = page.HasMore,
                    StatusMessage = Describe(page),
                    IsBrowseBusy = false,
                };
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsBrowseStale(generation))
            {
                Mutate(state => state with { IsBrowseBusy = false });
            }
        }
        catch (Exception exception)
        {
            if (IsBrowseStale(generation)) return;
            Mutate(state => state with
            {
                ErrorMessage = exception.GetBaseException().Message,
                IsBrowseBusy = false,
            });
        }
    }

    /// <summary>
    /// Opens title detail: identity, groups and the chapters of one group. Only
    /// one group is ever active; variants are never merged.
    /// </summary>
    public async Task OpenTitleAsync(
        RemoteTitleSummary summary,
        CancellationToken cancellationToken)
    {
        var source = CurrentSource();
        if (source is null) return;

        var generation = NextActionGeneration();
        Mutate(state => state with { IsActionBusy = true, ErrorMessage = null });

        try
        {
            var detail = await source.GetTitleAsync(summary.Identity, cancellationToken)
                .ConfigureAwait(false);
            var groups = await source.GetGroupsAsync(summary.Identity, cancellationToken)
                .ConfigureAwait(false);
            if (IsActionStale(generation)) return;

            if (groups.Count == 0)
            {
                Mutate(state => state with
                {
                    IsActionBusy = false,
                    ErrorMessage = "Title ini tidak punya group chapter yang tersedia.",
                });
                return;
            }

            Mutate(state => state with
            {
                IsDetailOpen = true,
                Detail = detail,
                Groups = groups,
                SelectedGroup = groups[0].Identity,
                Chapters = [],
                SelectedChapterIds = [],
                IsActionBusy = false,
                HasLibraryRoot = _hasLibraryRoot(),
            });

            await LoadChaptersAsync(source, summary.Identity, groups[0].Identity, generation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsActionStale(generation)) Mutate(state => state with { IsActionBusy = false });
        }
        catch (Exception exception)
        {
            if (IsActionStale(generation)) return;
            Mutate(state => state with
            {
                ErrorMessage = exception.GetBaseException().Message,
                IsActionBusy = false,
            });
        }
    }

    public async Task SelectGroupAsync(
        RemoteTitleIdentity title,
        RemoteSourceGroup group,
        CancellationToken cancellationToken)
    {
        var source = CurrentSource();
        if (source is null) return;

        var generation = NextActionGeneration();
        Mutate(state => state with
        {
            SelectedGroup = group.Identity,
            Chapters = [],
            SelectedChapterIds = [],
            IsActionBusy = true,
            ErrorMessage = null,
        });
        await LoadChaptersAsync(source, title, group.Identity, generation, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns to the exact prior grid and scroll anchor without a new fetch.
    ///
    /// Leaving the detail is a navigation like any other, so it wins over a detail
    /// action still in flight — reachable in practice by changing group and pressing
    /// Back before the chapters land. Without this the abandoned chapter load would
    /// repopulate <see cref="CatalogState.Chapters"/> for a detail that is already
    /// closed. The action busy half is released for the same reason a new Browse
    /// releases it: the abandoned action returns early without touching state.
    /// </summary>
    public void Back()
    {
        NextActionGeneration();
        Mutate(state => state with
        {
            IsDetailOpen = false,
            Detail = null,
            Groups = [],
            SelectedGroup = null,
            Chapters = [],
            SelectedChapterIds = [],
            IsActionBusy = false,
            ErrorMessage = null,
            HasLibraryRoot = _hasLibraryRoot(),
        });
    }

    /// <summary>
    /// Replaces the selection with exactly these chapter identities. The
    /// presentation supplies the visible set, because only it knows what a sort
    /// left on screen; this feature remains the owner of the selection the queue
    /// receives, and stores an immutable copy so a later UI change cannot mutate
    /// a snapshot already handed off.
    ///
    /// An unchanged selection is a no-op, so a checkbox click cannot start a
    /// render loop.
    /// </summary>
    public void SetSelectedChapterIds(IReadOnlyList<string> chapterIds)
    {
        ArgumentNullException.ThrowIfNull(chapterIds);

        lock (_gate)
        {
            if (_state.SelectedChapterIds.SequenceEqual(chapterIds, StringComparer.Ordinal))
            {
                return;
            }

            _state = _state with { SelectedChapterIds = [.. chapterIds] };
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection() =>
        Mutate(state => state with { SelectedChapterIds = [] });

    private async Task LoadChaptersAsync(
        IMangaSource source,
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        int actionGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var chapters = await source
                .GetChaptersAsync(title, group, cancellationToken)
                .ConfigureAwait(false);
            if (IsActionStale(actionGeneration)) return;

            Mutate(state => state with { Chapters = chapters, IsActionBusy = false });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsActionStale(actionGeneration)) Mutate(state => state with { IsActionBusy = false });
        }
        catch (Exception exception)
        {
            if (IsActionStale(actionGeneration)) return;
            Mutate(state => state with
            {
                ErrorMessage = exception.GetBaseException().Message,
                IsActionBusy = false,
            });
        }
    }

    private IMangaSource? CurrentSource()
    {
        string? sourceId;
        lock (_gate)
        {
            sourceId = _state.SelectedSourceId;
        }

        if (sourceId is null)
        {
            Mutate(state => state with
            {
                ErrorMessage = "Pilih satu web target lebih dulu.",
            });
            return null;
        }

        return _sources.Require(sourceId).Source;
    }

    private int NextBrowseGeneration() => Interlocked.Increment(ref _browseGeneration);

    private bool IsBrowseStale(int generation) =>
        Volatile.Read(ref _browseGeneration) != generation;

    private int NextActionGeneration() => Interlocked.Increment(ref _actionGeneration);

    private bool IsActionStale(int generation) =>
        Volatile.Read(ref _actionGeneration) != generation;

    private void Mutate(Func<CatalogState, CatalogState> map)
    {
        lock (_gate)
        {
            _state = map(_state);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(RemoteCatalogPage page)
    {
        if (page.Items.Count == 0)
        {
            return "Tidak ada hasil untuk query ini.";
        }

        return page.Total is { } total
            ? $"{page.Items.Count} hasil pada halaman {page.Page} dari {total}."
            : $"{page.Items.Count} hasil pada halaman {page.Page}.";
    }
}
