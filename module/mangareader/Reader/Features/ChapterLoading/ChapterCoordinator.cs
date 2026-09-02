using System.Collections.ObjectModel;
using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// The neighbor-loading seam the coordinator drives during active-surface
/// evaluation. Implemented by <see cref="ChapterPreloader"/>; kept as an
/// interface so the coordinator does not depend on the concrete preloader.
/// </summary>
internal interface IChapterNeighborPreloader
{
    Task EnsureNextFullAsync(int expectedActiveIndex, CancellationToken cancellationToken);
    Task EnsurePreviousWarmAsync(int expectedActiveIndex, CancellationToken cancellationToken);
    Task PromoteActiveToFullAsync(int expectedActiveIndex, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the rolling 3-surface collection (Previous/Active/Next), the active
/// chapter index, role/z-index promotion, viewport-anchor preservation when a
/// surface is inserted above the viewport, and active-surface evaluation. It
/// never decodes; it receives loaded content from the navigator and preloader.
/// </summary>
internal sealed class ChapterCoordinator
{
    private readonly IChapterLoadingRuntime _runtime;
    private readonly ObservableCollection<ChapterSurfaceModel> _surfaces = [];
    private readonly ReadOnlyObservableCollection<ChapterSurfaceModel> _readOnlySurfaces;

    private IChapterNeighborPreloader? _neighbors;
    private int _activeChapterIndex;
    private bool _readerReady;
    private bool _evaluationQueued;
    private bool _evaluationRunning;

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

    public void ClearSurfaces() => _surfaces.Clear();

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
        if (target is null || target.ChapterIndex == _activeChapterIndex) return;

        var previousActiveIndex = _activeChapterIndex;
        _runtime.State.SetTransitioning(true);
        _activeChapterIndex = target.ChapterIndex;
        UpdateSurfaceRoles();
        PublishActiveChapter();

        try
        {
            RemoveOutsideRollingWindow();
            if (_activeChapterIndex < previousActiveIndex)
            {
                await RequiredNeighbors.PromoteActiveToFullAsync(_activeChapterIndex, _runtime.Lifetime);
                await RequiredNeighbors.EnsurePreviousWarmAsync(_activeChapterIndex, _runtime.Lifetime);
                await RequiredNeighbors.EnsureNextFullAsync(_activeChapterIndex, _runtime.Lifetime);
            }
            else
            {
                await RequiredNeighbors.EnsureNextFullAsync(_activeChapterIndex, _runtime.Lifetime);
                await RequiredNeighbors.EnsurePreviousWarmAsync(_activeChapterIndex, _runtime.Lifetime);
            }
            UpdateSurfaceRoles();
        }
        catch (OperationCanceledException) when (_runtime.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_runtime.IsDisposed) _runtime.Status.SetNonBlockingDetail(exception.GetBaseException().Message);
        }
        finally
        {
            _runtime.State.SetTransitioning(false);
        }
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

    public void AddSurfaceOrdered(ChapterSurfaceModel surface)
    {
        var insertIndex = 0;
        while (insertIndex < _surfaces.Count
            && _surfaces[insertIndex].ChapterIndex < surface.ChapterIndex)
        {
            insertIndex++;
        }

        var insertedAboveViewport = insertIndex == 0 && _surfaces.Count > 0;
        var oldOffset = _runtime.Viewport.VerticalOffset;
        _surfaces.Insert(insertIndex, surface);
        _runtime.Viewport.UpdateLayout();
        if (insertedAboveViewport)
        {
            _runtime.Viewport.ScrollToVerticalOffset(
                oldOffset + RenderedHeight(surface),
                ReaderActivityOrigin.LayoutRestore);
        }
    }

    public void RemoveOutsideRollingWindow()
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
            var oldOffset = _runtime.Viewport.VerticalOffset;
            _surfaces.Remove(surface);
            _runtime.Viewport.UpdateLayout();
            if (removedAboveViewport)
            {
                _runtime.Viewport.ScrollToVerticalOffset(
                    Math.Max(0, oldOffset - removedHeight),
                    ReaderActivityOrigin.LayoutRestore);
            }
        }
    }

    public double TopOfSurface(int chapterIndex)
    {
        var top = 0d;
        foreach (var surface in _surfaces)
        {
            if (surface.ChapterIndex == chapterIndex) return top;
            top += RenderedHeight(surface);
        }
        return 0;
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
