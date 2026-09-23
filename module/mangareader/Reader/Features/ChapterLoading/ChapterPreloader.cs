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
    private readonly IChapterLoadingRuntime _runtime;
    private readonly ChapterCoordinator _coordinator;

    private ChapterRenderRequest? _fullRequest;
    private long _renderSizeGeneration;

    public ChapterPreloader(IChapterLoadingRuntime runtime, ChapterCoordinator coordinator)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _runtime.Viewport.SizeChanged += OnViewportSizeChanged;
    }

    public ChapterRenderRequest FullRequest =>
        _fullRequest ?? throw new InvalidOperationException("Reader render size is not configured.");

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
        return true;
    }

    public async Task EnsureNextFullAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var nextIndex = expectedActiveIndex + 1;
        if (_runtime.IsDisposed || nextIndex >= _runtime.Title.Chapters.Count) return;

        var existing = _coordinator.SurfaceAt(nextIndex);
        if (existing is not null)
        {
            // Never swap an existing surface's content in place: a quality swap
            // rebuilds its page images and reads as a sharpness pop mid-scroll.
            existing.SetRole(ChapterSurfaceRole.Next);
            return;
        }

        var content = await _runtime.LoadChapterAsync(nextIndex, FullRequest, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime.IsDisposed || _coordinator.ActiveChapterIndex != expectedActiveIndex) return;

        _coordinator.AddSurfaceOrdered(new ChapterSurfaceModel(
            nextIndex,
            content,
            ChapterSurfaceRole.Next));
    }

    public async Task EnsurePreviousWarmAsync(int expectedActiveIndex, CancellationToken cancellationToken)
    {
        var previousIndex = expectedActiveIndex - 1;
        if (_runtime.IsDisposed || previousIndex < 0) return;

        var existing = _coordinator.SurfaceAt(previousIndex);
        if (existing is not null)
        {
            // Same no-swap rule as the next surface: keep whatever it already has.
            existing.SetRole(ChapterSurfaceRole.Previous);
            return;
        }

        // Load the previous at full quality so the rolling window is uniformly
        // full-res and no preview<->full sharpness swap is ever needed.
        var content = await _runtime.LoadChapterAsync(previousIndex, FullRequest, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime.IsDisposed || _coordinator.ActiveChapterIndex != expectedActiveIndex) return;

        _coordinator.AddSurfaceOrdered(new ChapterSurfaceModel(
            previousIndex,
            content,
            ChapterSurfaceRole.Previous));
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
