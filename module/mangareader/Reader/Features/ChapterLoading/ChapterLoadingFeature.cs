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
    IReaderChapterNavigation,
    IChapterLoadingRuntime
{
    private readonly MangaTitle _title;
    private readonly ChapterInfo _initialChapter;
    private readonly IReaderChapterLoader _loader;
    private readonly IReaderStatusHost _status;
    private readonly ReaderSessionState _state;

    private readonly object _operationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _activeAsyncOperationCount;
    private bool _shutdownCancellationCompleted;
    private bool _asyncResourcesDisposed;
    private volatile bool _disposed;

    private IReaderViewport? _viewport;
    private ReaderActivityHub? _activity;
    private ReaderChapterNavigationHub? _hub;
    private ChapterCoordinator? _coordinator;
    private ChapterPreloader? _preloader;
    private ChapterNavigator? _navigator;

    /// <summary>
    /// Catalog path: the chapter-loading specifics are captured here; the viewport,
    /// activity hub, and navigation hub arrive through <see cref="Attach"/>.
    /// </summary>
    public ChapterLoadingFeature(
        MangaTitle title,
        ChapterInfo initialChapter,
        IReaderChapterLoader loader,
        IReaderStatusHost status,
        ReaderSessionState state)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        _initialChapter = initialChapter ?? throw new ArgumentNullException(nameof(initialChapter));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    /// Direct path used by tests and any host that already holds the viewport and
    /// hubs: wires the collaborators immediately instead of deferring to Attach.
    /// </summary>
    internal ChapterLoadingFeature(
        MangaTitle title,
        ChapterInfo initialChapter,
        IReaderViewport viewport,
        IReaderStatusHost status,
        ReaderSessionState state,
        ReaderActivityHub activity,
        IReaderChapterLoader loader,
        ReaderChapterNavigationHub? hub = null)
        : this(title, initialChapter, loader, status, state)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(activity);
        Wire(viewport, activity, hub);
    }

    public string FeatureName => "ChapterLoading";

    public void Attach(ReaderFeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Wire(context.Viewport, context.Activity, context.Chapters as ReaderChapterNavigationHub);
    }

    private void Wire(
        IReaderViewport viewport,
        ReaderActivityHub activity,
        ReaderChapterNavigationHub? hub)
    {
        if (_coordinator is not null)
            throw new InvalidOperationException("ChapterLoading is already attached.");

        _viewport = viewport;
        _activity = activity;
        _hub = hub;

        _coordinator = new ChapterCoordinator(this, _initialChapter);
        _preloader = new ChapterPreloader(this, _coordinator);
        _coordinator.Neighbors = _preloader;
        _navigator = new ChapterNavigator(this, _coordinator, _preloader);
        _coordinator.ActiveChapterChanged += OnCoordinatorActiveChapterChanged;

        hub?.RegisterImplementation(this, StartLoadAsync);
    }

    private ChapterCoordinator Coordinator =>
        _coordinator ?? throw new InvalidOperationException("ChapterLoading is not attached.");
    private ChapterPreloader Preloader =>
        _preloader ?? throw new InvalidOperationException("ChapterLoading is not attached.");
    private ChapterNavigator Navigator =>
        _navigator ?? throw new InvalidOperationException("ChapterLoading is not attached.");

    /// <summary>Triggers the first chapter load; called by the host once the viewport has a width.</summary>
    public Task StartLoadAsync() => Navigator.StartLoadAsync();

    // ---- IReaderChapterNavigation (delegated to the collaborators) ----

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    public IReadOnlyList<ChapterInfo> Chapters => _title.Chapters;
    public ReadOnlyObservableCollection<ChapterSurfaceModel> Surfaces => Coordinator.Surfaces;
    public int ActiveChapterIndex => Coordinator.ActiveChapterIndex;
    public ChapterInfo ActiveChapter => Coordinator.ActiveChapter;
    public string MangaTitle => _title.Title;
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

    MangaTitle IChapterLoadingRuntime.Title => _title;
    ReaderSessionState IChapterLoadingRuntime.State => _state;
    IReaderStatusHost IChapterLoadingRuntime.Status => _status;
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
            return await _loader.LoadAsync(
                _title.Chapters[chapterIndex],
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

        _hub?.UnregisterImplementation();
    }
}
