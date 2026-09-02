using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Owns the initial load and latest-request-wins chapter jumps: it decodes the
/// target chapter (or reuses a full-quality surface), commits it as the sole
/// active surface, restores the top-of-chapter anchor, and asks the preloader to
/// warm the neighbors. Cancellation is generation-based so a newer jump supersedes
/// an in-flight older one without a use-after-dispose.
/// </summary>
internal sealed class ChapterNavigator
{
    private readonly IChapterLoadingRuntime _runtime;
    private readonly ChapterCoordinator _coordinator;
    private readonly ChapterPreloader _preloader;

    private CancellationTokenSource? _navigationCancellation;
    private long _navigationGeneration;
    private bool _loadStarted;

    public ChapterNavigator(
        IChapterLoadingRuntime runtime,
        ChapterCoordinator coordinator,
        ChapterPreloader preloader)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _preloader = preloader ?? throw new ArgumentNullException(nameof(preloader));
    }

    public Task StartLoadAsync() => _runtime.RunTrackedAsync(StartLoadCoreAsync);

    private async Task StartLoadCoreAsync()
    {
        if (_loadStarted || _runtime.IsDisposed) return;
        _loadStarted = true;

        _runtime.State.SetError(false);
        _runtime.State.SetLoading(true);
        _runtime.Activity.Report(ReaderActivityOrigin.Loading);
        _runtime.Status.ShowLoading("Preparing chapter", "Reading CBZ...");

        var progress = new Progress<ChapterLoadProgress>(_runtime.Status.ReportProgress);
        try
        {
            _runtime.Viewport.UpdateLayout();
            if (!_preloader.TryConfigureRenderRequests())
            {
                throw new InvalidOperationException(
                    "Reader viewport does not have a usable width yet.");
            }

            var activeIndex = _coordinator.ActiveChapterIndex;
            var content = await _runtime.LoadChapterAsync(
                activeIndex,
                _preloader.FullRequest,
                progress,
                _runtime.Lifetime);
            if (_runtime.IsDisposed) return;

            _coordinator.AddActiveSurface(activeIndex, content);
            _runtime.Viewport.UpdateLayout();
            _runtime.Viewport.ScrollToVerticalOffset(0, ReaderActivityOrigin.LayoutRestore);

            await PrepareInitialNeighborsAsync(activeIndex, _runtime.Lifetime);
            if (_runtime.IsDisposed) return;

            // WPF UpdateLayout can dispatch ScrollChanged reentrantly while a
            // previous surface is being inserted above the requested chapter.
            // Automatic active-surface evaluation must not observe that
            // transient, pre-anchor layout. Publish readiness only after all
            // initial neighbors and the preserved anchor are final.
            _coordinator.MarkReaderReady();
            _runtime.State.SetLoading(false);
            _runtime.Status.Hide();
            _coordinator.QueueViewportEvaluation();
        }
        catch (OperationCanceledException) when (_runtime.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_runtime.IsDisposed) return;
            _runtime.State.SetLoading(false);
            _runtime.State.SetError(true);
            _runtime.Status.ShowError(exception.GetBaseException().Message);
        }
    }

    public Task NavigateToChapterAsync(int index)
    {
        if (index < 0 || index >= _runtime.Title.Chapters.Count) return Task.CompletedTask;
        return _runtime.RunTrackedAsync(() => NavigateToChapterCoreAsync(index));
    }

    private async Task NavigateToChapterCoreAsync(int index)
    {
        if (_runtime.IsDisposed) return;

        var generation = Interlocked.Increment(ref _navigationGeneration);

        if (index == _coordinator.ActiveChapterIndex && _coordinator.SurfaceAt(index) is not null)
        {
            Interlocked.Exchange(ref _navigationCancellation, null)?.Cancel();
            _runtime.State.SetError(false);
            _runtime.State.SetLoading(false);
            _runtime.State.SetTransitioning(false);
            _runtime.Status.Hide();
            _runtime.Activity.Report(ReaderActivityOrigin.ChapterJump);
            _runtime.Viewport.ScrollToVerticalOffset(
                _coordinator.TopOfSurface(index),
                ReaderActivityOrigin.ChapterJump);
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_runtime.Lifetime);
        var token = cancellation.Token;
        var previous = Interlocked.Exchange(ref _navigationCancellation, cancellation);
        previous?.Cancel();

        _runtime.State.SetError(false);
        _runtime.State.SetLoading(true);
        _runtime.State.SetTransitioning(true);
        _runtime.Activity.Report(ReaderActivityOrigin.ChapterJump);
        _runtime.Status.ShowLoading("Preparing chapter", _runtime.Title.Chapters[index].Title);
        var progress = new Progress<ChapterLoadProgress>(_runtime.Status.ReportProgress);

        try
        {
            var existing = _coordinator.SurfaceAt(index);
            var content = existing is { IsFullQuality: true }
                ? new LoadedChapter(
                    existing.Chapter,
                    existing.Pages,
                    existing.SurfaceWidth,
                    existing.SurfaceHeight,
                    0,
                    PageRenderQuality.Full)
                : await _runtime.LoadChapterAsync(index, _preloader.FullRequest, progress, cancellation.Token);

            if (!IsCurrentNavigation(generation, token)) return;

            _coordinator.ClearSurfaces();
            _coordinator.AddActiveSurface(index, content);
            _coordinator.SetActiveIndex(index);
            _coordinator.MarkReaderReady();
            _runtime.Viewport.UpdateLayout();
            _runtime.Viewport.ScrollToVerticalOffset(0, ReaderActivityOrigin.ChapterJump);
            _runtime.State.SetLoading(false);
            _runtime.State.SetTransitioning(false);
            _runtime.Status.Hide();
            _coordinator.PublishActiveChapter();

            await PrepareNeighborsAsync(index, generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsCurrentNavigation(generation, CancellationToken.None)) return;
            _runtime.State.SetLoading(false);
            _runtime.State.SetTransitioning(false);
            _runtime.State.SetError(true);
            _runtime.Status.ShowError(exception.GetBaseException().Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref _navigationCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    public void CancelPendingNavigation()
    {
        var navigation = Interlocked.Exchange(ref _navigationCancellation, null);
        try
        {
            navigation?.Cancel();
        }
        catch (AggregateException)
        {
        }
    }

    private async Task PrepareNeighborsAsync(
        int expectedActiveIndex,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _preloader.EnsureNextFullAsync(expectedActiveIndex, cancellationToken);
            if (!IsCurrentNavigation(generation, cancellationToken)) return;
            await _preloader.EnsurePreviousWarmAsync(expectedActiveIndex, cancellationToken);
            if (IsCurrentNavigation(generation, cancellationToken)) _coordinator.UpdateSurfaceRoles();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentNavigation(generation, CancellationToken.None))
                _runtime.Status.SetNonBlockingDetail(exception.GetBaseException().Message);
        }
    }

    private async Task PrepareInitialNeighborsAsync(
        int expectedActiveIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            await _preloader.EnsureNextFullAsync(expectedActiveIndex, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            if (!_runtime.IsDisposed)
                _runtime.Status.SetNonBlockingDetail(
                    $"Next chapter unavailable: {exception.GetBaseException().Message}");
        }

        try
        {
            await _preloader.EnsurePreviousWarmAsync(expectedActiveIndex, cancellationToken);
            _coordinator.UpdateSurfaceRoles();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_runtime.IsDisposed)
                _runtime.Status.SetNonBlockingDetail(
                    $"Previous chapter unavailable: {exception.GetBaseException().Message}");
        }
    }

    private bool IsCurrentNavigation(long generation, CancellationToken cancellationToken) =>
        !_runtime.IsDisposed
        && !cancellationToken.IsCancellationRequested
        && Volatile.Read(ref _navigationGeneration) == generation;
}
