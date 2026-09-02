using System.Collections.ObjectModel;
using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Shared infrastructure the ChapterLoading collaborators run on: the load gate
/// that serializes decodes, the feature lifetime, and the tracked-async disposal
/// protocol that guarantees no use-after-dispose. Owned by ChapterLoadingFeature.
/// </summary>
internal interface IChapterLoadingRuntime
{
    MangaTitle Title { get; }
    IReaderViewport Viewport { get; }
    ReaderSessionState State { get; }
    IReaderStatusHost Status { get; }
    ReaderActivityHub Activity { get; }
    bool IsDisposed { get; }
    CancellationToken Lifetime { get; }

    Task RunTrackedAsync(Func<Task> operation);

    Task<LoadedChapter> LoadChapterAsync(
        int chapterIndex,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Plug-and-play chapter loading: the 3-surface rolling window, boundary
/// preloading, and latest-request-wins jumps. ReaderWindow never sees these
/// internals; it consumes IReaderChapterNavigation through the hub this feature
/// registers into. The feature owns the shared async runtime the coordinator,
/// preloader, and navigator collaborate on.
/// </summary>
public sealed class ChapterLoadingFeature :
    IReaderFeature,
    IReaderStartableFeature,
    IReaderChapterNavigation,
    IChapterLoadingRuntime
{
    private MangaTitle? _title;
    private ChapterInfo? _initialChapter;
    private IReaderChapterLoader? _loader;
    private IReaderStatusHost? _status;
    private ReaderSessionState? _state;

    private readonly object _operationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _activeAsyncOperationCount;
    private bool _shutdownCancellationCompleted;
    private bool _asyncResourcesDisposed;
    private bool _registered;
    private volatile bool _disposed;

    private IReaderViewport? _viewport;
    private ReaderActivityHub? _activity;
    private IReaderChapterNavigationRegistry? _registry;
    private ChapterCoordinator? _coordinator;
    private ChapterPreloader? _preloader;
    private ChapterNavigator? _navigator;

    /// <summary>
    /// Catalog path: all dependencies arrive through <see cref="Attach"/>.
    /// </summary>
    public ChapterLoadingFeature() { }

    /// <summary>
    /// Direct path used by tests and any host that already holds the viewport and
    /// services: wires the collaborators immediately instead of deferring to Attach.
    /// </summary>
    internal ChapterLoadingFeature(
        MangaTitle title,
        ChapterInfo initialChapter,
        IReaderViewport viewport,
        IReaderStatusHost status,
        ReaderSessionState state,
        ReaderActivityHub activity,
        IReaderChapterLoader loader,
        IReaderChapterNavigationRegistry? registry = null)
    {
        Configure(new ReaderContentContext(title, initialChapter, loader, status), state);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(activity);
        Wire(viewport, activity, registry);
    }

    public string FeatureName => "ChapterLoading";

    public void Attach(ReaderFeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Configure(context.Content, context.SessionState);
        Wire(context.Viewport, context.Activity, context.ChapterNavigationRegistry);
    }

    private void Configure(ReaderContentContext content, ReaderSessionState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_title is not null)
            throw new InvalidOperationException("ChapterLoading is already configured.");

        ArgumentNullException.ThrowIfNull(content);
        _title = content.Title;
        _initialChapter = content.InitialChapter;
        _loader = content.ChapterLoader;
        _status = content.Status;
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    private void Wire(
        IReaderViewport viewport,
        ReaderActivityHub activity,
        IReaderChapterNavigationRegistry? registry)
    {
        if (_coordinator is not null)
            throw new InvalidOperationException("ChapterLoading is already attached.");

        _viewport = viewport;
        _activity = activity;
        _registry = registry;

        _coordinator = new ChapterCoordinator(this, InitialChapter);
        _preloader = new ChapterPreloader(this, _coordinator);
        _coordinator.Neighbors = _preloader;
        _navigator = new ChapterNavigator(this, _coordinator, _preloader);
        _coordinator.ActiveChapterChanged += OnCoordinatorActiveChapterChanged;

        if (registry is not null)
        {
            registry.Register(this);
            _registered = true;
        }
    }

    private ChapterCoordinator Coordinator =>
        _coordinator ?? throw new InvalidOperationException("ChapterLoading is not attached.");
    private ChapterPreloader Preloader =>
        _preloader ?? throw new InvalidOperationException("ChapterLoading is not attached.");
    private ChapterNavigator Navigator =>
        _navigator ?? throw new InvalidOperationException("ChapterLoading is not attached.");

    /// <summary>Triggers the first chapter load; called by the host once the viewport has a width.</summary>
    public Task StartAsync() => Navigator.StartLoadAsync();

    internal Task StartLoadAsync() => StartAsync();

    // ---- IReaderChapterNavigation (delegated to the collaborators) ----

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    public IReadOnlyList<ChapterInfo> Chapters => Title.Chapters;
    public ReadOnlyObservableCollection<ChapterSurfaceModel> Surfaces => Coordinator.Surfaces;
    public int ActiveChapterIndex => Coordinator.ActiveChapterIndex;
    public ChapterInfo ActiveChapter => Coordinator.ActiveChapter;
    public string MangaTitle => Title.Title;
    public bool CanNavigatePrevious => Coordinator.CanNavigatePrevious;
    public bool CanNavigateNext => Coordinator.CanNavigateNext;
    public bool IsAtAbsoluteBeginning => Coordinator.IsAtAbsoluteBeginning;
    public bool IsAtAbsoluteEnd => Coordinator.IsAtAbsoluteEnd;

    public Task NavigateToChapterAsync(int index) => Navigator.NavigateToChapterAsync(index);

    public Task PrepareBoundaryAsync(int direction, CancellationToken cancellationToken) =>
        Preloader.PrepareBoundaryAsync(direction, cancellationToken);

    public void NotifyZoomChanged() => Coordinator.NotifyZoomChanged();

    private void OnCoordinatorActiveChapterChanged(
        object? sender,
        OpenChapterRequestedEventArgs e) =>
        ActiveChapterChanged?.Invoke(this, e);

    // ---- IChapterLoadingRuntime ----

    MangaTitle IChapterLoadingRuntime.Title => Title;
    ReaderSessionState IChapterLoadingRuntime.State => State;
    IReaderStatusHost IChapterLoadingRuntime.Status => Status;
    bool IChapterLoadingRuntime.IsDisposed => _disposed;
    CancellationToken IChapterLoadingRuntime.Lifetime => _lifetime.Token;

    IReaderViewport IChapterLoadingRuntime.Viewport =>
        _viewport ?? throw new InvalidOperationException("ChapterLoading is not attached.");
    ReaderActivityHub IChapterLoadingRuntime.Activity =>
        _activity ?? throw new InvalidOperationException("ChapterLoading is not attached.");

    Task IChapterLoadingRuntime.RunTrackedAsync(Func<Task> operation) => RunTrackedAsync(operation);

    async Task<LoadedChapter> IChapterLoadingRuntime.LoadChapterAsync(
        int chapterIndex,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            return await Loader.LoadAsync(
                Title.Chapters[chapterIndex],
                request,
                progress,
                cancellationToken);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    internal bool AsyncResourcesDisposed
    {
        get
        {
            lock (_operationGate) return _asyncResourcesDisposed;
        }
    }

    internal int ActiveAsyncOperationCount
    {
        get
        {
            lock (_operationGate) return _activeAsyncOperationCount;
        }
    }

    private async Task RunTrackedAsync(Func<Task> operation)
    {
        if (!TryBeginAsyncOperation()) return;
        try
        {
            await operation();
        }
        finally
        {
            EndAsyncOperation();
        }
    }

    private bool TryBeginAsyncOperation()
    {
        lock (_operationGate)
        {
            if (_disposed) return false;
            _activeAsyncOperationCount++;
            return true;
        }
    }

    private void EndAsyncOperation()
    {
        var disposeResources = false;
        lock (_operationGate)
        {
            if (_activeAsyncOperationCount <= 0)
                throw new InvalidOperationException("Reader async operation ownership is unbalanced.");
            _activeAsyncOperationCount--;
            disposeResources = MarkAsyncResourcesForDisposalIfDrained();
        }

        if (disposeResources) DisposeAsyncResources();
    }

    private bool MarkAsyncResourcesForDisposalIfDrained()
    {
        if (!_disposed
            || !_shutdownCancellationCompleted
            || _activeAsyncOperationCount != 0
            || _asyncResourcesDisposed)
        {
            return false;
        }

        _asyncResourcesDisposed = true;
        return true;
    }

    private void DisposeAsyncResources()
    {
        _lifetime.Dispose();
        _loadGate.Dispose();
    }

    public void Dispose()
    {
        lock (_operationGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _preloader?.BeginShutdown();
        _coordinator?.DetachViewport();
        _preloader?.DetachViewport();
        _navigator?.CancelPendingNavigation();

        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException)
        {
        }

        _coordinator?.ClearSurfaces();

        var disposeResources = false;
        lock (_operationGate)
        {
            _shutdownCancellationCompleted = true;
            disposeResources = MarkAsyncResourcesForDisposalIfDrained();
        }
        if (disposeResources) DisposeAsyncResources();

        if (_registered && _registry is not null)
        {
            _registry.Unregister(this);
            _registered = false;
        }
    }

    private MangaTitle Title =>
        _title ?? throw new InvalidOperationException("ChapterLoading is not configured.");
    private ChapterInfo InitialChapter =>
        _initialChapter ?? throw new InvalidOperationException("ChapterLoading is not configured.");
    private IReaderChapterLoader Loader =>
        _loader ?? throw new InvalidOperationException("ChapterLoading is not configured.");
    private IReaderStatusHost Status =>
        _status ?? throw new InvalidOperationException("ChapterLoading is not configured.");
    private ReaderSessionState State =>
        _state ?? throw new InvalidOperationException("ChapterLoading is not configured.");
}
