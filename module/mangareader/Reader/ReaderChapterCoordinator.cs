using System.Collections.ObjectModel;
using System.Windows;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public interface IReaderChapterLoader
{
    Task<LoadedChapter> LoadAsync(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sole owner of chapter loading, rolling surfaces, active chapter commits,
/// viewport-anchor preservation, and latest-request-wins chapter jumps.
/// </summary>
public sealed class ReaderChapterCoordinator : IReaderChapterNavigation, IDisposable
{
    private const int PreviewPixelWidth = 220;
    private const int PreviousFullQualityTailPages = 4;

    private readonly MangaTitle _title;
    private readonly IReaderViewport _viewport;
    private readonly IReaderStatusHost _status;
    private readonly ReaderSessionState _state;
    private readonly ReaderActivityHub _activity;
    private readonly IReaderChapterLoader _loader;
    private readonly object _operationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly ObservableCollection<ChapterSurfaceModel> _surfaces = [];
    private readonly ReadOnlyObservableCollection<ChapterSurfaceModel> _readOnlySurfaces;

    private ChapterRenderRequest? _fullRequest;
    private ChapterRenderRequest? _previousRequest;
    private CancellationTokenSource? _navigationCancellation;
    private long _navigationGeneration;
    private int _activeChapterIndex;
    private bool _loadStarted;
    private bool _readerReady;
    private bool _evaluationQueued;
    private bool _evaluationRunning;
    private volatile bool _disposed;
    private bool _shutdownCancellationCompleted;
    private bool _asyncResourcesDisposed;
    private int _activeAsyncOperationCount;
    private long _renderSizeGeneration;

    public ReaderChapterCoordinator(
        MangaTitle title,
        ChapterInfo initialChapter,
        IReaderViewport viewport,
        IReaderStatusHost status,
        ReaderSessionState state,
        ReaderActivityHub activity,
        IReaderChapterLoader loader)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        ArgumentNullException.ThrowIfNull(initialChapter);
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _activeChapterIndex = FindChapterIndex(title.Chapters, initialChapter);
        _readOnlySurfaces = new ReadOnlyObservableCollection<ChapterSurfaceModel>(_surfaces);

        _viewport.Changed += OnViewportChanged;
        _viewport.SizeChanged += OnViewportSizeChanged;
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    public IReadOnlyList<ChapterInfo> Chapters => _title.Chapters;
    public ReadOnlyObservableCollection<ChapterSurfaceModel> Surfaces => _readOnlySurfaces;
    public int ActiveChapterIndex => _activeChapterIndex;
    public ChapterInfo ActiveChapter => _title.Chapters[_activeChapterIndex];
    public string MangaTitle => _title.Title;
    public bool CanNavigatePrevious => _activeChapterIndex > 0;
    public bool CanNavigateNext => _activeChapterIndex + 1 < _title.Chapters.Count;
    public bool IsAtAbsoluteBeginning =>
        _activeChapterIndex == 0 && _viewport.VerticalOffset <= 0.5;
    public bool IsAtAbsoluteEnd =>
        _activeChapterIndex == _title.Chapters.Count - 1
        && _viewport.VerticalOffset >= Math.Max(0, _viewport.ScrollableHeight - 0.5);
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

    public Task StartLoadAsync() => RunTrackedAsync(StartLoadCoreAsync);

    private async Task StartLoadCoreAsync()
    {
        if (_loadStarted || _disposed) return;
        _loadStarted = true;

        _state.SetError(false);
        _state.SetLoading(true);
        _activity.Report(ReaderActivityOrigin.Loading);
        _status.ShowLoading("Preparing chapter", "Reading CBZ...");

        var progress = new Progress<ChapterLoadProgress>(_status.ReportProgress);
        try
        {
            _viewport.UpdateLayout();
            if (!TryConfigureRenderRequests())
            {
                throw new InvalidOperationException(
                    "Reader viewport does not have a usable width yet.");
            }

            var content = await LoadChapterAsync(
                _activeChapterIndex,
                FullRequest,
                progress,
                _lifetime.Token);
            if (_disposed) return;

            _surfaces.Add(new ChapterSurfaceModel(
                _activeChapterIndex,
                content,
                ChapterSurfaceRole.Active));
            _viewport.UpdateLayout();
            _viewport.ScrollToVerticalOffset(0, ReaderActivityOrigin.LayoutRestore);

            await PrepareInitialNeighborsAsync(_activeChapterIndex, _lifetime.Token);
            if (_disposed) return;

            // WPF UpdateLayout can dispatch ScrollChanged reentrantly while a
            // previous surface is being inserted above the requested chapter.
            // Automatic active-surface evaluation must not observe that
            // transient, pre-anchor layout. Publish readiness only after all
            // initial neighbors and the preserved anchor are final.
            _readerReady = true;
            _state.SetLoading(false);
            _status.Hide();
            QueueViewportEvaluation();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            _state.SetLoading(false);
            _state.SetError(true);
            _status.ShowError(exception.GetBaseException().Message);
        }
    }

    public Task NavigateToChapterAsync(int index)
    {
        if (index < 0 || index >= _title.Chapters.Count) return Task.CompletedTask;
        return RunTrackedAsync(() => NavigateToChapterCoreAsync(index));
    }

    private async Task NavigateToChapterCoreAsync(int index)
    {
        if (_disposed) return;

        var generation = Interlocked.Increment(ref _navigationGeneration);

        if (index == _activeChapterIndex && SurfaceAt(index) is not null)
        {
            Interlocked.Exchange(ref _navigationCancellation, null)?.Cancel();
            _state.SetError(false);
            _state.SetLoading(false);
            _state.SetTransitioning(false);
            _status.Hide();
            _activity.Report(ReaderActivityOrigin.ChapterJump);
            _viewport.ScrollToVerticalOffset(TopOfSurface(index), ReaderActivityOrigin.ChapterJump);
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = cancellation.Token;
        var previous = Interlocked.Exchange(ref _navigationCancellation, cancellation);
        previous?.Cancel();

        _state.SetError(false);
        _state.SetLoading(true);
        _state.SetTransitioning(true);
        _activity.Report(ReaderActivityOrigin.ChapterJump);
        _status.ShowLoading("Preparing chapter", _title.Chapters[index].Title);
        var progress = new Progress<ChapterLoadProgress>(_status.ReportProgress);

        try
        {
            var existing = SurfaceAt(index);
            var content = existing is { IsFullQuality: true }
                ? new LoadedChapter(
                    existing.Chapter,
                    existing.Pages,
                    existing.SurfaceWidth,
                    existing.SurfaceHeight,
                    0,
                    PageRenderQuality.Full)
                : await LoadChapterAsync(index, FullRequest, progress, cancellation.Token);

            if (!IsCurrentNavigation(generation, token)) return;

            _surfaces.Clear();
            _surfaces.Add(new ChapterSurfaceModel(index, content, ChapterSurfaceRole.Active));
            _activeChapterIndex = index;
            _readerReady = true;
            _viewport.UpdateLayout();
            _viewport.ScrollToVerticalOffset(0, ReaderActivityOrigin.ChapterJump);
            _state.SetLoading(false);
            _state.SetTransitioning(false);
            _status.Hide();
            PublishActiveChapter();

            await PrepareNeighborsAsync(index, generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsCurrentNavigation(generation, CancellationToken.None)) return;
            _state.SetLoading(false);
            _state.SetTransitioning(false);
            _state.SetError(true);
            _status.ShowError(exception.GetBaseException().Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref _navigationCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    public Task PrepareBoundaryAsync(int direction, CancellationToken cancellationToken)
    {
        if (direction == 0) return Task.CompletedTask;
        return RunTrackedAsync(() => PrepareBoundaryCoreAsync(direction, cancellationToken));
    }

    private async Task PrepareBoundaryCoreAsync(
        int direction,
        CancellationToken cancellationToken)
    {
        if (_disposed || !_readerReady) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);

        if (direction > 0 && CanNavigateNext)
            await EnsureNextFullAsync(_activeChapterIndex, linked.Token);
        else if (direction < 0 && CanNavigatePrevious)
            await EnsurePreviousWarmAsync(_activeChapterIndex, linked.Token);
    }

    public void NotifyZoomChanged()
    {
        if (_disposed) return;
        _viewport.UpdateLayout();
        QueueViewportEvaluation();
    }

    private void OnViewportChanged(object? sender, ReaderViewportChangedEventArgs e)
    {
        if (_disposed || e.Origin == ReaderActivityOrigin.LayoutRestore) return;
        QueueViewportEvaluation();
    }

    private void OnViewportSizeChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var generation = Interlocked.Increment(ref _renderSizeGeneration);
        _ = _viewport.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (!_disposed && generation == Volatile.Read(ref _renderSizeGeneration))
                    _ = TryConfigureRenderRequests();
            }));
    }

    private void QueueViewportEvaluation()
    {
        if (!_readerReady || _disposed)
            return;

        _evaluationQueued = true;
        if (_evaluationRunning) return;

        _ = _viewport.Dispatcher.InvokeAsync(
            EvaluateQueuedViewportAsync,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private Task EvaluateQueuedViewportAsync() => RunTrackedAsync(EvaluateQueuedViewportCoreAsync);

    private async Task EvaluateQueuedViewportCoreAsync()
    {
        if (_evaluationRunning || _disposed) return;
        _evaluationRunning = true;
        try
        {
            while (_evaluationQueued && !_disposed)
            {
                _evaluationQueued = false;
                await EvaluateActiveSurfaceAsync();
            }
        }
        finally
        {
            _evaluationRunning = false;
        }
    }

    private async Task EvaluateActiveSurfaceAsync()
    {
        if (!_readerReady || _state.IsLoading || _state.IsTransitioning || _disposed) return;

        var target = SurfaceAtViewportCenter();
        if (target is null || target.ChapterIndex == _activeChapterIndex) return;

        var previousActiveIndex = _activeChapterIndex;
        _state.SetTransitioning(true);
        _activeChapterIndex = target.ChapterIndex;
        UpdateSurfaceRoles();
        PublishActiveChapter();

        try
        {
            RemoveOutsideRollingWindow();
            if (_activeChapterIndex < previousActiveIndex)
            {
                await PromoteActiveToFullAsync(_activeChapterIndex, _lifetime.Token);
                await EnsurePreviousWarmAsync(_activeChapterIndex, _lifetime.Token);
                await EnsureNextFullAsync(_activeChapterIndex, _lifetime.Token);
            }
            else
            {
                await EnsureNextFullAsync(_activeChapterIndex, _lifetime.Token);
                await EnsurePreviousWarmAsync(_activeChapterIndex, _lifetime.Token);
            }
            UpdateSurfaceRoles();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed) _status.SetNonBlockingDetail(exception.GetBaseException().Message);
        }
        finally
        {
            _state.SetTransitioning(false);
        }
    }

    private async Task PrepareNeighborsAsync(
        int expectedActiveIndex,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureNextFullAsync(expectedActiveIndex, cancellationToken);
            if (!IsCurrentNavigation(generation, cancellationToken)) return;
            await EnsurePreviousWarmAsync(expectedActiveIndex, cancellationToken);
            if (IsCurrentNavigation(generation, cancellationToken)) UpdateSurfaceRoles();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentNavigation(generation, CancellationToken.None))
                _status.SetNonBlockingDetail(exception.GetBaseException().Message);
        }
    }

    private async Task PrepareInitialNeighborsAsync(
        int expectedActiveIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureNextFullAsync(expectedActiveIndex, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            if (!_disposed)
                _status.SetNonBlockingDetail(
                    $"Next chapter unavailable: {exception.GetBaseException().Message}");
        }

        try
        {
            await EnsurePreviousWarmAsync(expectedActiveIndex, cancellationToken);
            UpdateSurfaceRoles();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed)
                _status.SetNonBlockingDetail(
                    $"Previous chapter unavailable: {exception.GetBaseException().Message}");
        }
    }

    private async Task<LoadedChapter> LoadChapterAsync(
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

    private async Task EnsureNextFullAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var nextIndex = expectedActiveIndex + 1;
        if (_disposed || nextIndex >= _title.Chapters.Count) return;

        var existing = SurfaceAt(nextIndex);
        if (existing is { IsFullQuality: true })
        {
            existing.SetRole(ChapterSurfaceRole.Next);
            return;
        }

        var content = await LoadChapterAsync(nextIndex, FullRequest, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _activeChapterIndex != expectedActiveIndex) return;

        if (existing is not null)
        {
            existing.ReplaceContent(content);
            existing.SetRole(ChapterSurfaceRole.Next);
        }
        else
        {
            AddSurfaceOrdered(new ChapterSurfaceModel(
                nextIndex,
                content,
                ChapterSurfaceRole.Next));
        }
    }

    private async Task EnsurePreviousWarmAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var previousIndex = expectedActiveIndex - 1;
        if (_disposed || previousIndex < 0) return;

        var existing = SurfaceAt(previousIndex);
        if (existing is { IsFullQuality: false })
        {
            existing.SetRole(ChapterSurfaceRole.Previous);
            return;
        }

        var content = await LoadChapterAsync(previousIndex, PreviousRequest, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _activeChapterIndex != expectedActiveIndex) return;

        if (existing is not null)
        {
            existing.ReplaceContent(content);
            existing.SetRole(ChapterSurfaceRole.Previous);
        }
        else
        {
            AddSurfaceOrdered(new ChapterSurfaceModel(
                previousIndex,
                content,
                ChapterSurfaceRole.Previous));
        }
    }

    private async Task PromoteActiveToFullAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var active = SurfaceAt(expectedActiveIndex);
        if (active is null || active.IsFullQuality) return;
        var content = await LoadChapterAsync(expectedActiveIndex, FullRequest, null, cancellationToken);
        if (!_disposed && _activeChapterIndex == expectedActiveIndex)
            active.ReplaceContent(content);
    }

    private ChapterSurfaceModel? SurfaceAtViewportCenter()
    {
        if (_surfaces.Count == 0) return null;
        var center = _viewport.VerticalOffset + (_viewport.ViewportHeight / 2);
        var top = 0d;
        foreach (var surface in _surfaces)
        {
            var height = RenderedHeight(surface);
            if (center < top + height) return surface;
            top += height;
        }
        return _surfaces[^1];
    }

    private void AddSurfaceOrdered(ChapterSurfaceModel surface)
    {
        var insertIndex = 0;
        while (insertIndex < _surfaces.Count
            && _surfaces[insertIndex].ChapterIndex < surface.ChapterIndex)
        {
            insertIndex++;
        }

        var insertedAboveViewport = insertIndex == 0 && _surfaces.Count > 0;
        var oldOffset = _viewport.VerticalOffset;
        _surfaces.Insert(insertIndex, surface);
        _viewport.UpdateLayout();
        if (insertedAboveViewport)
        {
            _viewport.ScrollToVerticalOffset(
                oldOffset + RenderedHeight(surface),
                ReaderActivityOrigin.LayoutRestore);
        }
    }

    private void RemoveOutsideRollingWindow()
    {
        var minimumIndex = _activeChapterIndex - 1;
        var maximumIndex = _activeChapterIndex + 1;
        foreach (var surface in _surfaces
            .Where(candidate => candidate.ChapterIndex < minimumIndex
                || candidate.ChapterIndex > maximumIndex)
            .ToArray())
        {
            var removedAboveViewport = surface.ChapterIndex < _activeChapterIndex;
            var removedHeight = RenderedHeight(surface);
            var oldOffset = _viewport.VerticalOffset;
            _surfaces.Remove(surface);
            _viewport.UpdateLayout();
            if (removedAboveViewport)
            {
                _viewport.ScrollToVerticalOffset(
                    Math.Max(0, oldOffset - removedHeight),
                    ReaderActivityOrigin.LayoutRestore);
            }
        }
    }

    private double TopOfSurface(int chapterIndex)
    {
        var top = 0d;
        foreach (var surface in _surfaces)
        {
            if (surface.ChapterIndex == chapterIndex) return top;
            top += RenderedHeight(surface);
        }
        return 0;
    }

    private double RenderedHeight(ChapterSurfaceModel surface)
    {
        if (_viewport.ItemContainerFor(surface) is { ActualHeight: > 0 } container)
            return container.ActualHeight;
        return surface.SurfaceHeight * _state.ZoomScale;
    }

    private void UpdateSurfaceRoles()
    {
        foreach (var surface in _surfaces)
        {
            surface.SetRole(surface.ChapterIndex.CompareTo(_activeChapterIndex) switch
            {
                < 0 => ChapterSurfaceRole.Previous,
                > 0 => ChapterSurfaceRole.Next,
                _ => ChapterSurfaceRole.Active,
            });
        }
    }

    private ChapterSurfaceModel? SurfaceAt(int chapterIndex) =>
        _surfaces.FirstOrDefault(surface => surface.ChapterIndex == chapterIndex);

    private bool TryConfigureRenderRequests()
    {
        var availableWidth = _viewport.ViewportWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 32)
            availableWidth = _viewport.InputElement.ActualWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 32)
            return false;

        _viewport.SetContentWidth(availableWidth);
        var dpiScale = Math.Max(0.1, _viewport.DpiScale);
        var displayMaximumPixelWidth = Math.Max(
            1,
            (int)Math.Floor(Math.Max(1, availableWidth - 32) * dpiScale));
        _fullRequest = new ChapterRenderRequest(
            displayMaximumPixelWidth,
            displayMaximumPixelWidth,
            dpiScale,
            PageRenderQuality.Full);
        _previousRequest = new ChapterRenderRequest(
            Math.Min(PreviewPixelWidth, displayMaximumPixelWidth),
            displayMaximumPixelWidth,
            dpiScale,
            PageRenderQuality.Preview,
            PreviousFullQualityTailPages);
        return true;
    }

    private ChapterRenderRequest FullRequest =>
        _fullRequest ?? throw new InvalidOperationException("Reader render size is not configured.");

    private ChapterRenderRequest PreviousRequest =>
        _previousRequest ?? throw new InvalidOperationException("Reader render size is not configured.");

    private bool IsCurrentNavigation(long generation, CancellationToken cancellationToken) =>
        !_disposed
        && !cancellationToken.IsCancellationRequested
        && Volatile.Read(ref _navigationGeneration) == generation;

    private void PublishActiveChapter() =>
        ActiveChapterChanged?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(_title, ActiveChapter));

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

        Interlocked.Increment(ref _renderSizeGeneration);
        _viewport.Changed -= OnViewportChanged;
        _viewport.SizeChanged -= OnViewportSizeChanged;
        var navigation = Interlocked.Exchange(ref _navigationCancellation, null);
        try
        {
            navigation?.Cancel();
        }
        catch (AggregateException)
        {
        }
        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException)
        {
        }
        _surfaces.Clear();

        var disposeResources = false;
        lock (_operationGate)
        {
            _shutdownCancellationCompleted = true;
            disposeResources = MarkAsyncResourcesForDisposalIfDrained();
        }
        if (disposeResources) DisposeAsyncResources();
    }

    private static int FindChapterIndex(
        IReadOnlyList<ChapterInfo> chapters,
        ChapterInfo chapter)
    {
        for (var index = 0; index < chapters.Count; index++)
        {
            if (string.Equals(
                chapters[index].FilePath,
                chapter.FilePath,
                StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new ArgumentException("The chapter does not belong to this title.", nameof(chapter));
    }
}
