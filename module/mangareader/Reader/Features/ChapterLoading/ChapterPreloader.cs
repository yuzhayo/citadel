using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Owns render-request sizing and neighbor loading: the next chapter at full
/// quality, the previous chapter as a preview with a full-quality tail, boundary
/// preparation, and reconfiguration when the viewport is resized. It never
/// decides the active chapter; it reads that from the coordinator and writes
/// surfaces back through it.
/// </summary>
internal sealed class ChapterPreloader : IChapterNeighborPreloader
{
    private const int PreviewPixelWidth = 220;
    private const int PreviousFullQualityTailPages = 4;

    private readonly IChapterLoadingRuntime _runtime;
    private readonly ChapterCoordinator _coordinator;

    private ChapterRenderRequest? _fullRequest;
    private ChapterRenderRequest? _previousRequest;
    private long _renderSizeGeneration;

    public ChapterPreloader(IChapterLoadingRuntime runtime, ChapterCoordinator coordinator)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _runtime.Viewport.SizeChanged += OnViewportSizeChanged;
    }

    public ChapterRenderRequest FullRequest =>
        _fullRequest ?? throw new InvalidOperationException("Reader render size is not configured.");

    private ChapterRenderRequest PreviousRequest =>
        _previousRequest ?? throw new InvalidOperationException("Reader render size is not configured.");

    public bool TryConfigureRenderRequests()
    {
        var availableWidth = _runtime.Viewport.ViewportWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 32)
            availableWidth = _runtime.Viewport.InputElement.ActualWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 32)
            return false;

        _runtime.Viewport.SetContentWidth(availableWidth);
        var dpiScale = Math.Max(0.1, _runtime.Viewport.DpiScale);
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

    public async Task EnsureNextFullAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var nextIndex = expectedActiveIndex + 1;
        if (_runtime.IsDisposed || nextIndex >= _runtime.Title.Chapters.Count) return;

        var existing = _coordinator.SurfaceAt(nextIndex);
        if (existing is { IsFullQuality: true })
        {
            existing.SetRole(ChapterSurfaceRole.Next);
            return;
        }

        var content = await _runtime.LoadChapterAsync(nextIndex, FullRequest, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime.IsDisposed || _coordinator.ActiveChapterIndex != expectedActiveIndex) return;

        if (existing is not null)
        {
            existing.ReplaceContent(content);
            existing.SetRole(ChapterSurfaceRole.Next);
        }
        else
        {
            _coordinator.AddSurfaceOrdered(new ChapterSurfaceModel(
                nextIndex,
                content,
                ChapterSurfaceRole.Next));
        }
    }

    public async Task EnsurePreviousWarmAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var previousIndex = expectedActiveIndex - 1;
        if (_runtime.IsDisposed || previousIndex < 0) return;

        var existing = _coordinator.SurfaceAt(previousIndex);
        if (existing is { IsFullQuality: false })
        {
            existing.SetRole(ChapterSurfaceRole.Previous);
            return;
        }

        var content = await _runtime.LoadChapterAsync(previousIndex, PreviousRequest, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime.IsDisposed || _coordinator.ActiveChapterIndex != expectedActiveIndex) return;

        if (existing is not null)
        {
            existing.ReplaceContent(content);
            existing.SetRole(ChapterSurfaceRole.Previous);
        }
        else
        {
            _coordinator.AddSurfaceOrdered(new ChapterSurfaceModel(
                previousIndex,
                content,
                ChapterSurfaceRole.Previous));
        }
    }

    public async Task PromoteActiveToFullAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var active = _coordinator.SurfaceAt(expectedActiveIndex);
        if (active is null || active.IsFullQuality) return;
        var content = await _runtime.LoadChapterAsync(expectedActiveIndex, FullRequest, null, cancellationToken);
        if (!_runtime.IsDisposed && _coordinator.ActiveChapterIndex == expectedActiveIndex)
            active.ReplaceContent(content);
    }

    public Task PrepareBoundaryAsync(int direction, CancellationToken cancellationToken)
    {
        if (direction == 0) return Task.CompletedTask;
        return _runtime.RunTrackedAsync(() => PrepareBoundaryCoreAsync(direction, cancellationToken));
    }

    private async Task PrepareBoundaryCoreAsync(int direction, CancellationToken cancellationToken)
    {
        if (_runtime.IsDisposed || !_coordinator.ReaderReady) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtime.Lifetime);

        if (direction > 0 && _coordinator.CanNavigateNext)
            await EnsureNextFullAsync(_coordinator.ActiveChapterIndex, linked.Token);
        else if (direction < 0 && _coordinator.CanNavigatePrevious)
            await EnsurePreviousWarmAsync(_coordinator.ActiveChapterIndex, linked.Token);
    }

    private void OnViewportSizeChanged(object? sender, EventArgs e)
    {
        if (_runtime.IsDisposed) return;
        var generation = Interlocked.Increment(ref _renderSizeGeneration);
        _ = _runtime.Viewport.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (!_runtime.IsDisposed && generation == Volatile.Read(ref _renderSizeGeneration))
                    _ = TryConfigureRenderRequests();
            }));
    }

    public void BeginShutdown() => Interlocked.Increment(ref _renderSizeGeneration);

    public void DetachViewport() => _runtime.Viewport.SizeChanged -= OnViewportSizeChanged;
}
