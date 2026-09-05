using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Features.Downloader.Catalog;

/// <summary>
/// One immutable Catalog snapshot. The screen renders it; it never mutates it.
/// </summary>
public sealed record CatalogState
{
    public string? SelectedSourceId { get; init; }

    public string? SelectedSourceName { get; init; }

    public bool IsBusy { get; init; }

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
    private int _generation;
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
        Mutate(state =>
        {
            if (registration is null)
            {
                return state with
                {
                    SelectedSourceId = null,
                    SelectedSourceName = null,
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
                IsBusy = false,
            });
            return;
        }

        var request = new RemoteBrowseRequest(
            string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Page: 1,
            filterState?.CurrentFilter);
        _activeRequest = request;
        var generation = NextGeneration();

        Mutate(state => state with
        {
            IsBusy = true,
            ErrorMessage = null,
            StatusMessage = "Mengambil katalog…",
            HasLibraryRoot = _hasLibraryRoot(),
        });

        try
        {
            var page = await source.BrowseAsync(request, cancellationToken).ConfigureAwait(false);
            if (IsStale(generation)) return;

            Mutate(state => state with
            {
                Results = page.Items,
                TotalCount = page.Total,
                Page = page.Page,
                CanLoadMore = page.HasMore,
                IsDetailOpen = false,
                Detail = null,
                StatusMessage = Describe(page),
                IsBusy = false,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsStale(generation))
            {
                Mutate(state => state with { IsBusy = false, StatusMessage = null });
            }
        }
        catch (Exception exception)
        {
            if (IsStale(generation)) return;
            Mutate(state => state with
            {
                // The previous successful result set stays visible.
                ErrorMessage = exception.GetBaseException().Message,
                IsBusy = false,
                StatusMessage = null,
            });
        }
    }

    /// <summary>Appends the next page for the current query snapshot only.</summary>
    public async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        var source = CurrentSource();
        var request = _activeRequest;
        if (source is null || request is null) return;

        var next = request with { Page = request.Page + 1 };
        var generation = NextGeneration();
        Mutate(state => state with { IsBusy = true, ErrorMessage = null });

        try
        {
            var page = await source.BrowseAsync(next, cancellationToken).ConfigureAwait(false);
            if (IsStale(generation)) return;

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
                    IsBusy = false,
                };
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsStale(generation))
            {
                Mutate(state => state with { IsBusy = false });
            }
        }
        catch (Exception exception)
        {
            if (IsStale(generation)) return;
            Mutate(state => state with
            {
                ErrorMessage = exception.GetBaseException().Message,
                IsBusy = false,
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

        var generation = NextGeneration();
        Mutate(state => state with { IsBusy = true, ErrorMessage = null });

        try
        {
            var detail = await source.GetTitleAsync(summary.Identity, cancellationToken)
                .ConfigureAwait(false);
            var groups = await source.GetGroupsAsync(summary.Identity, cancellationToken)
                .ConfigureAwait(false);
            if (IsStale(generation)) return;

            if (groups.Count == 0)
            {
                Mutate(state => state with
                {
                    IsBusy = false,
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
                IsBusy = false,
                HasLibraryRoot = _hasLibraryRoot(),
            });

            await LoadChaptersAsync(source, summary.Identity, groups[0].Identity, generation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsStale(generation)) Mutate(state => state with { IsBusy = false });
        }
        catch (Exception exception)
        {
            if (IsStale(generation)) return;
            Mutate(state => state with
            {
                ErrorMessage = exception.GetBaseException().Message,
                IsBusy = false,
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

        var generation = NextGeneration();
        Mutate(state => state with
        {
            SelectedGroup = group.Identity,
            Chapters = [],
            SelectedChapterIds = [],
            IsBusy = true,
            ErrorMessage = null,
        });
        await LoadChaptersAsync(source, title, group.Identity, generation, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns to the exact prior grid and scroll anchor without a new fetch.
    /// </summary>
    public void Back() => Mutate(state => state with
    {
        IsDetailOpen = false,
        Detail = null,
        Groups = [],
        SelectedGroup = null,
        Chapters = [],
        SelectedChapterIds = [],
        ErrorMessage = null,
        HasLibraryRoot = _hasLibraryRoot(),
    });

    public void ToggleChapter(RemoteChapterSummary chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        Mutate(state =>
        {
            var selected = new List<string>(state.SelectedChapterIds);
            if (!selected.Remove(chapter.Identity.ChapterId))
            {
                selected.Add(chapter.Identity.ChapterId);
            }

            return state with { SelectedChapterIds = selected };
        });
    }

    public void ClearSelection() =>
        Mutate(state => state with { SelectedChapterIds = [] });

    private async Task LoadChaptersAsync(
        IMangaSource source,
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var chapters = await source
                .GetChaptersAsync(title, group, cancellationToken)
                .ConfigureAwait(false);
            if (IsStale(generation)) return;

            Mutate(state => state with { Chapters = chapters, IsBusy = false });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsStale(generation)) Mutate(state => state with { IsBusy = false });
        }
        catch (Exception exception)
        {
            if (IsStale(generation)) return;
            Mutate(state => state with
            {
                ErrorMessage = exception.GetBaseException().Message,
                IsBusy = false,
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

    private int NextGeneration() => Interlocked.Increment(ref _generation);

    private bool IsStale(int generation) => Volatile.Read(ref _generation) != generation;

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
