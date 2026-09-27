using System.Collections.ObjectModel;
using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// The neighbour-loading seam the coordinator drives during active-surface
/// evaluation. Implementations return a new surface for the caller to insert, or
/// null when the neighbour is already present. Keeping insert ownership in the
/// coordinator is what lets one boundary crossing cost a single layout pass.
/// </summary>
internal interface IChapterNeighborPreloader
{
    Task<ChapterSurfaceModel?> PrepareNextAsync(int expectedActiveIndex, CancellationToken cancellationToken);
    Task<ChapterSurfaceModel?> PreparePreviousAsync(int expectedActiveIndex, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the rolling 3-surface collection (Previous/Active/Next), the active
/// chapter index, role/z-index promotion, viewport-anchor preservation, and
/// active-surface evaluation. It never decodes; it receives loaded content from
/// the navigator and preloader.
/// <para>
/// Promotion does not lock scroll input. Neighbours are decoded before changing
/// the strip; forward preload waits for page two. The commit captures the latest
/// anchor and restores it only when content above the viewport changes.
/// </para>
/// </summary>
internal sealed class ChapterCoordinator
{
    private readonly IChapterLoadingRuntime _runtime;
    private readonly ObservableCollection<ChapterSurfaceModel> _surfaces = [];
    private readonly ReadOnlyObservableCollection<ChapterSurfaceModel> _readOnlySurfaces;
    private readonly List<ChapterSurfaceModel> _pendingEviction = [];

    private IChapterNeighborPreloader? _neighbors;
    private int _activeChapterIndex;
    private bool _evictionFlushQueued;
    private bool _readerReady;
    private bool _evaluationQueued;
    private bool _evaluationRunning;
    private long _navigationEpoch;

    public void InvalidatePreload() => _navigationEpoch++;

    public ChapterCoordinator(IChapterLoadingRuntime runtime, ChapterInfo initialChapter)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(initialChapter);
        _activeChapterIndex = FindChapterIndex(runtime.Title.Chapters, initialChapter);
        _readOnlySurfaces = new ReadOnlyObservableCollection<ChapterSurfaceModel>(_surfaces);
        _runtime.Viewport.Changed += OnViewportChanged;
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    /// <summary>Set by the feature after the preloader exists; breaks the coordinator/preloader cycle.</summary>
    public IChapterNeighborPreloader? Neighbors
    {
        get => _neighbors;
        set => _neighbors = value;
    }

    public ReadOnlyObservableCollection<ChapterSurfaceModel> Surfaces => _readOnlySurfaces;
    public int ActiveChapterIndex => _activeChapterIndex;
    public ChapterInfo ActiveChapter => _runtime.Title.Chapters[_activeChapterIndex];
    public bool CanNavigatePrevious => _activeChapterIndex > 0;
    public bool CanNavigateNext => _activeChapterIndex + 1 < _runtime.Title.Chapters.Count;
    public bool IsAtAbsoluteBeginning =>
        _activeChapterIndex == 0 && _runtime.Viewport.VerticalOffset <= 0.5;
    public bool IsAtAbsoluteEnd =>
        _activeChapterIndex == _runtime.Title.Chapters.Count - 1
        && _runtime.Viewport.VerticalOffset >= Math.Max(0, _runtime.Viewport.ScrollableHeight - 0.5);

    public bool ReaderReady => _readerReady;

    public void SetActiveIndex(int index) => _activeChapterIndex = index;
    public void MarkReaderReady() => _readerReady = true;

    public void AddActiveSurface(int chapterIndex, LoadedChapter content) =>
        _surfaces.Add(new ChapterSurfaceModel(chapterIndex, content, ChapterSurfaceRole.Active));

    public IReadOnlyList<ChapterSurfaceModel> DetachAllSurfaces()
    {
        // Detach first, then release. Evicting while the surface is still bound
        // would make the ItemsControl re-read an empty Pages collection for a strip
        // that is about to disappear.
        var detached = _surfaces.ToArray();
        _surfaces.Clear();
        return detached;
    }

    public void ReleaseDetachedAfterLayout(IReadOnlyList<ChapterSurfaceModel> detached) =>
        ScheduleDeferredEviction(detached);

    public void ClearSurfacesForShutdown()
    {
        var detached = DetachAllSurfaces();
        FlushPendingEviction();
        foreach (var surface in detached)
        {
            surface.Evict();
        }
    }

    public void PublishActiveChapter() =>
        ActiveChapterChanged?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(_runtime.Title, ActiveChapter));

    public void NotifyZoomChanged()
    {
        if (_runtime.IsDisposed) return;
        _runtime.Viewport.UpdateLayout();
        QueueViewportEvaluation();
    }

    public void DetachViewport() => _runtime.Viewport.Changed -= OnViewportChanged;

    private IChapterNeighborPreloader RequiredNeighbors =>
        _neighbors ?? throw new InvalidOperationException("Chapter preloader is not wired.");

    private void OnViewportChanged(object? sender, ReaderViewportChangedEventArgs e)
    {
        if (_runtime.IsDisposed || e.Origin == ReaderActivityOrigin.LayoutRestore) return;
        QueueViewportEvaluation();
    }

    public void QueueViewportEvaluation()
    {
        if (!_readerReady || _runtime.IsDisposed)
            return;

        _evaluationQueued = true;
        if (_evaluationRunning) return;

        _ = _runtime.Viewport.Dispatcher.InvokeAsync(
            EvaluateQueuedViewportAsync,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private Task EvaluateQueuedViewportAsync() =>
        _runtime.RunTrackedAsync(EvaluateQueuedViewportCoreAsync);

    private async Task EvaluateQueuedViewportCoreAsync()
    {
        if (_evaluationRunning || _runtime.IsDisposed) return;
        _evaluationRunning = true;
        try
        {
            while (_evaluationQueued && !_runtime.IsDisposed)
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
        if (!_readerReady || _runtime.State.IsLoading || _runtime.State.IsTransitioning || _runtime.IsDisposed)
            return;

        var target = SurfaceAtViewportCenter();
        if (target is null) return;

        var previousActiveIndex = _activeChapterIndex;
        if (target.ChapterIndex != _activeChapterIndex)
        {
            _activeChapterIndex = target.ChapterIndex;
            UpdateSurfaceRoles();
            PublishActiveChapter();
        }

        // Promotion is cheap. Forward decoding starts only when the top of the
        // viewport reaches page two, not when the chapter first enters the screen.
        var prepareNext = HasReachedPreloadPoint(target) && CanNavigateNext
            && SurfaceAt(_activeChapterIndex + 1) is null;
        var preparePrevious = CanNavigatePrevious && SurfaceAt(_activeChapterIndex - 1) is null;
        if (!prepareNext && !preparePrevious && !OutsideRollingWindow().Any()) return;
        var expectedIndex = _activeChapterIndex;
        var epoch = _navigationEpoch;
        var decodeStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        IReadOnlyList<ChapterSurfaceModel> detached = [];
        try
        {
            // Resolve the neighbour on the side the reader is heading towards first, so a
            // chapter the reader is scrolling towards is never the second wait.
            ChapterSurfaceModel? entering = null;
            ChapterSurfaceModel? trailing = null;
            if (_activeChapterIndex < previousActiveIndex)
            {
                if (preparePrevious) entering = await RequiredNeighbors.PreparePreviousAsync(
                    expectedIndex, _runtime.Lifetime);
                if (!IsCurrentPreload()) return;
                if (prepareNext) trailing = await RequiredNeighbors.PrepareNextAsync(
                    expectedIndex, _runtime.Lifetime);
            }
            else
            {
                if (prepareNext) trailing = await RequiredNeighbors.PrepareNextAsync(
                    expectedIndex, _runtime.Lifetime);
                if (!IsCurrentPreload()) return;
                if (preparePrevious) entering = await RequiredNeighbors.PreparePreviousAsync(
                    expectedIndex, _runtime.Lifetime);
            }

            if (!IsCurrentPreload()) return;
            System.Diagnostics.Debug.WriteLine($"[Reader] preload decode/wait: {System.Diagnostics.Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds:F1}ms");

            // Anchored here, not at the top of the method. The loads above await,
            // and the momentum glide writes VerticalOffset every vsync while they
            // do. An anchor sampled before that would drag the reader backwards to
            // wherever they were when the boundary was first noticed, which reads
            // as a jump every time a chapter is entered during a fast scroll. The
            // preloads above only read the collection, so deferring the detach to
            // here is safe.
            var captured = CaptureScrollAnchor();
            var layoutStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            detached = DetachOutsideRollingWindow();
            if (entering is not null && SurfaceAt(entering.ChapterIndex) is null) InsertSurfaceOrdered(entering);
            if (trailing is not null && SurfaceAt(trailing.ChapterIndex) is null) InsertSurfaceOrdered(trailing);
            // Appending the next chapter below the reader does not move the
            // current content. Forcing a full ScrollViewer layout here makes
            // every page in the new chapter materialize in one UI frame.
            // A removal or insertion before the current chapter still needs
            // synchronous layout so its scroll anchor can be restored.
            if (detached.Count > 0 || entering is not null)
            {
                _runtime.Viewport.UpdateLayout();
                RestoreScrollAnchor(captured);
            }
            UpdateSurfaceRoles();
            System.Diagnostics.Debug.WriteLine($"[Reader] preload attach/layout: {System.Diagnostics.Stopwatch.GetElapsedTime(layoutStarted).TotalMilliseconds:F1}ms");
        }
        catch (OperationCanceledException) when (_runtime.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentPreload()) _runtime.Status.SetNonBlockingDetail(exception.GetBaseException().Message);
        }
        finally
        {
            ScheduleDeferredEviction(detached);
        }

        bool IsCurrentPreload() => !_runtime.IsDisposed && !_runtime.State.IsLoading
            && epoch == _navigationEpoch && ReferenceEquals(SurfaceAt(expectedIndex), target)
            && SurfaceAtViewportCenter()?.ChapterIndex == expectedIndex;
    }

    private bool HasReachedPreloadPoint(ChapterSurfaceModel surface)
    {
        var firstPageHeight = surface.Pages.Count > 1
            ? surface.Pages[0].DisplayHeight * _runtime.State.ZoomScale : 0d;
        var threshold = TopOfSurface(surface.ChapterIndex) + firstPageHeight;
        // A short final surface can hit the scroll limit before page two reaches
        // the top. Start there as well, otherwise the next chapter is unreachable.
        return _runtime.Viewport.VerticalOffset >= threshold - 0.5
            || _runtime.Viewport.VerticalOffset >= _runtime.Viewport.ScrollableHeight - 0.5;
    }

    public ChapterSurfaceModel? SurfaceAtViewportCenter()
    {
        if (_surfaces.Count == 0) return null;
        var center = _runtime.Viewport.VerticalOffset + (_runtime.Viewport.ViewportHeight / 2);
        var top = 0d;
        foreach (var surface in _surfaces)
        {
            var height = RenderedHeight(surface);
            if (center < top + height) return surface;
            top += height;
        }
        return _surfaces[^1];
    }

    /// <summary>
    /// Inserts and lays out on its own. Used by the initial load and the overlay
    /// boundary step, where the caller has no pending surface mutations to batch.
    /// </summary>
    public void AddSurfaceOrdered(ChapterSurfaceModel surface)
    {
        var anchor = CaptureScrollAnchor();
        InsertSurfaceOrdered(surface);
        _runtime.Viewport.UpdateLayout();
        RestoreScrollAnchor(anchor);
    }

    private void InsertSurfaceOrdered(ChapterSurfaceModel surface)
    {
        var insertIndex = 0;
        while (insertIndex < _surfaces.Count
            && _surfaces[insertIndex].ChapterIndex < surface.ChapterIndex)
        {
            insertIndex++;
        }

        _surfaces.Insert(insertIndex, surface);
    }

    private IReadOnlyList<ChapterSurfaceModel> DetachOutsideRollingWindow()
    {
        var detached = OutsideRollingWindow().ToArray();

        foreach (var surface in detached)
        {
            _surfaces.Remove(surface);
        }

        return detached;
    }

    private IEnumerable<ChapterSurfaceModel> OutsideRollingWindow() => _surfaces
        .Where(candidate => Math.Abs(candidate.ChapterIndex - _activeChapterIndex) > 1)
        .Where(candidate => TopOfSurface(candidate.ChapterIndex) + RenderedHeight(candidate)
                <= _runtime.Viewport.VerticalOffset
            || TopOfSurface(candidate.ChapterIndex)
                >= _runtime.Viewport.VerticalOffset + _runtime.Viewport.ViewportHeight);

    /// <summary>
    /// Holds the detached surfaces' bitmaps until the forced layout is over. Releasing
    /// them inside the boundary transaction would drop a chapter's worth of unmanaged
    /// pixel buffers in the same frame as the layout; the surfaces are already out of
    /// the visual tree here, so the eviction itself cannot invalidate layout.
    /// <para>
    /// The hold ends when the dispatcher next reaches background work, which is after
    /// the transaction's own layout pass has completed. It deliberately does not wait
    /// for another scroll event, so an idle reader is not holding a detached chapter.
    /// At most one boundary's worth is ever held, so a run of chapters shorter than
    /// the viewport cannot stack retained chapters.
    /// </para>
    /// </summary>
    private void ScheduleDeferredEviction(IReadOnlyList<ChapterSurfaceModel> detached)
    {
        if (detached.Count == 0) return;

        if (_pendingEviction.Count > 0) FlushPendingEviction();
        _pendingEviction.AddRange(detached);

        if (_runtime.IsDisposed || _evictionFlushQueued) return;
        _evictionFlushQueued = true;
        _ = _runtime.Viewport.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _evictionFlushQueued = false;
                if (_runtime.IsDisposed) return;
                FlushPendingEviction();
            }));
    }

    private void FlushPendingEviction()
    {
        var held = _pendingEviction.ToArray();
        _pendingEviction.Clear();

        foreach (var surface in held)
        {
            surface.Evict();
        }
    }

    public double TopOfSurface(int chapterIndex) =>
        TryTopOfSurface(chapterIndex, out var top) ? top : 0;

    private bool TryTopOfSurface(int chapterIndex, out double top)
    {
        var running = 0d;
        foreach (var surface in _surfaces)
        {
            if (surface.ChapterIndex == chapterIndex)
            {
                top = running;
                return true;
            }

            running += RenderedHeight(surface);
        }

        top = 0;
        return false;
    }

    private readonly record struct ScrollAnchor(int ChapterIndex, double OffsetWithinSurface);

    private ScrollAnchor CaptureScrollAnchor()
    {
        if (_surfaces.Count == 0) return new ScrollAnchor(_activeChapterIndex, 0d);

        var offset = _runtime.Viewport.VerticalOffset;
        var top = 0d;
        foreach (var surface in _surfaces)
        {
            var height = RenderedHeight(surface);
            if (offset < top + height) return new ScrollAnchor(surface.ChapterIndex, offset - top);
            top += height;
        }

        var last = _surfaces[^1];
        return new ScrollAnchor(last.ChapterIndex, offset - top);
    }

    private void RestoreScrollAnchor(ScrollAnchor anchor)
    {
        var found = TryTopOfSurface(anchor.ChapterIndex, out var top);

        // When the anchored chapter left the window the reader was inside it, and
        // that removal is exactly what moved the content down, so the top of the
        // strip is the matching position.
        var target = found ? Math.Max(0, top + anchor.OffsetWithinSurface) : 0d;
        if (Math.Abs(target - _runtime.Viewport.VerticalOffset) <= 0.5) return;

        _runtime.Viewport.ScrollToVerticalOffset(target, ReaderActivityOrigin.LayoutRestore);
    }

    public double RenderedHeight(ChapterSurfaceModel surface)
    {
        if (_runtime.Viewport.ItemContainerFor(surface) is { ActualHeight: > 0 } container)
            return container.ActualHeight;
        return surface.SurfaceHeight * _runtime.State.ZoomScale;
    }

    public void UpdateSurfaceRoles()
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

    public ChapterSurfaceModel? SurfaceAt(int chapterIndex) =>
        _surfaces.FirstOrDefault(surface => surface.ChapterIndex == chapterIndex);

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
