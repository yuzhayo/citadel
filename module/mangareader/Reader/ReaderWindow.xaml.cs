using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class ReaderWindow : Window
{
    private const int PreviewPixelWidth = 220;
    private const int PreviousFullQualityTailPages = 4;

    private readonly MangaTitle _title;
    private readonly CbzChapterLoader _loader = new();
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly SemaphoreSlim _chapterLoadGate = new(1, 1);
    private readonly ObservableCollection<ChapterSurfaceModel> _surfaces = new();
    private readonly int _initialChapterIndex;
    private ReaderZoomController? _zoomController;

    private ChapterRenderRequest? _fullRequest;
    private ChapterRenderRequest? _previousRequest;
    private int _activeChapterIndex;
    private bool _loadStarted;
    private bool _readerReady;
    private bool _transitionInProgress;
    private bool _closed;

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    public ReaderWindow(MangaTitle title, ChapterInfo chapter)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        ArgumentNullException.ThrowIfNull(chapter);

        _initialChapterIndex = FindChapterIndex(title.Chapters, chapter);
        _activeChapterIndex = _initialChapterIndex;

        InitializeComponent();
        _zoomController = new ReaderZoomController(
            ReaderScroller,
            () => ZoomScale,
            scale => ZoomScale = scale);
        ChapterList.ItemsSource = _surfaces;
        UpdateWindowTitle();
    }

    public static readonly DependencyProperty ZoomScaleProperty = DependencyProperty.Register(
        nameof(ZoomScale),
        typeof(double),
        typeof(ReaderWindow),
        new PropertyMetadata(ReaderZoomController.DefaultScale));

    public double ZoomScale
    {
        get => (double)GetValue(ZoomScaleProperty);
        private set => SetValue(ZoomScaleProperty, value);
    }

    private async void ReaderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadStarted) return;
        _loadStarted = true;

        ConfigureRenderRequests();
        var progress = new Progress<ChapterLoadProgress>(UpdateProgress);

        try
        {
            var activeContent = await LoadChapterAsync(
                _activeChapterIndex,
                FullRequest,
                progress);
            if (_closed) return;

            _surfaces.Add(new ChapterSurfaceModel(
                _activeChapterIndex,
                activeContent,
                ChapterSurfaceRole.Active));

            ReaderScroller.Visibility = Visibility.Visible;
            ReaderScroller.IsEnabled = true;
            ReaderScroller.UpdateLayout();
            LoadingPanel.Visibility = Visibility.Collapsed;
            ReaderScroller.ScrollToTop();
            ReaderScroller.Focus();
            _readerReady = true;

            // A is already interactive. B is prepared silently and appended as
            // soon as every page is ready.
            _transitionInProgress = true;
            try
            {
                await EnsureNextFullAsync();
            }
            catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (!_closed)
                {
                    Title = $"{_title.Title} — {_title.Chapters[_activeChapterIndex].Title} — next unavailable: {exception.GetBaseException().Message}";
                }
            }
            finally
            {
                _transitionInProgress = false;
            }

            await EvaluateActiveSurfaceAsync();
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_closed) return;
            ShowError(exception.GetBaseException().Message);
        }
    }

    private async Task<LoadedChapter> LoadChapterAsync(
        int chapterIndex,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress = null)
    {
        await _chapterLoadGate.WaitAsync(_loadCancellation.Token);
        try
        {
            return await _loader.LoadAsync(
                _title.Chapters[chapterIndex],
                request,
                progress,
                _loadCancellation.Token);
        }
        finally
        {
            _chapterLoadGate.Release();
        }
    }

    private async Task EnsureNextFullAsync()
    {
        var nextIndex = _activeChapterIndex + 1;
        if (_closed || nextIndex >= _title.Chapters.Count) return;

        var existing = SurfaceAt(nextIndex);
        if (existing is not null)
        {
            if (!existing.IsFullQuality)
            {
                existing.ReplaceContent(await LoadChapterAsync(nextIndex, FullRequest));
            }
            existing.SetRole(ChapterSurfaceRole.Next);
            return;
        }

        var content = await LoadChapterAsync(nextIndex, FullRequest);
        if (_closed) return;
        AddSurfaceOrdered(new ChapterSurfaceModel(
            nextIndex,
            content,
            ChapterSurfaceRole.Next));
    }

    private async Task EnsurePreviousWarmAsync()
    {
        var previousIndex = _activeChapterIndex - 1;
        if (_closed || previousIndex < 0) return;

        var existing = SurfaceAt(previousIndex);
        if (existing is not null)
        {
            existing.SetRole(ChapterSurfaceRole.Previous);
            if (existing.IsFullQuality)
            {
                existing.ReplaceContent(await LoadChapterAsync(previousIndex, PreviousRequest));
            }
            return;
        }

        var content = await LoadChapterAsync(previousIndex, PreviousRequest);
        if (_closed) return;
        AddSurfaceOrdered(new ChapterSurfaceModel(
            previousIndex,
            content,
            ChapterSurfaceRole.Previous));
    }

    private async Task PromoteActiveToFullAsync()
    {
        var active = SurfaceAt(_activeChapterIndex);
        if (active is null || active.IsFullQuality) return;
        active.ReplaceContent(await LoadChapterAsync(_activeChapterIndex, FullRequest));
    }

    private async Task EvaluateActiveSurfaceAsync()
    {
        if (!_readerReady || _transitionInProgress || _closed) return;

        var target = SurfaceAtViewportCenter();
        if (target is null || target.ChapterIndex == _activeChapterIndex) return;

        _transitionInProgress = true;
        var previousActiveIndex = _activeChapterIndex;
        _activeChapterIndex = target.ChapterIndex;
        UpdateSurfaceRoles();
        UpdateWindowTitle();
        ActiveChapterChanged?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(
                _title,
                _title.Chapters[_activeChapterIndex]));

        try
        {
            RemoveOutsideRollingWindow();

            if (_activeChapterIndex < previousActiveIndex)
            {
                // Reverse direction: the warm previous is already visible. Its
                // full cache gets priority before any farther-back preview.
                await PromoteActiveToFullAsync();
                await EnsurePreviousWarmAsync();
                await EnsureNextFullAsync();
            }
            else
            {
                // Forward direction: keep the next boundary seamless first,
                // then release most of the previous chapter's bitmap memory.
                await EnsureNextFullAsync();
                await EnsurePreviousWarmAsync();
            }

            UpdateSurfaceRoles();
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                Title = $"{_title.Title} — {_title.Chapters[_activeChapterIndex].Title} — {exception.GetBaseException().Message}";
            }
        }
        finally
        {
            _transitionInProgress = false;
        }

        if (!_closed) await EvaluateActiveSurfaceAsync();
    }

    private ChapterSurfaceModel? SurfaceAtViewportCenter()
    {
        if (_surfaces.Count == 0) return null;

        var center = ReaderScroller.VerticalOffset + (ReaderScroller.ViewportHeight / 2);
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
        var oldOffset = ReaderScroller.VerticalOffset;
        _surfaces.Insert(insertIndex, surface);
        ReaderScroller.UpdateLayout();

        if (insertedAboveViewport)
        {
            ReaderScroller.ScrollToVerticalOffset(
                oldOffset + RenderedHeight(surface));
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
            var oldOffset = ReaderScroller.VerticalOffset;
            _surfaces.Remove(surface);
            ReaderScroller.UpdateLayout();

            if (removedAboveViewport)
            {
                ReaderScroller.ScrollToVerticalOffset(
                    Math.Max(0, oldOffset - removedHeight));
            }
        }
    }

    private double RenderedHeight(ChapterSurfaceModel surface)
    {
        if (ChapterList.ItemContainerGenerator.ContainerFromItem(surface)
            is FrameworkElement { ActualHeight: > 0 } container)
        {
            return container.ActualHeight;
        }

        return surface.SurfaceHeight * ZoomScale;
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

    private void ConfigureRenderRequests()
    {
        var availableWidth = ReaderScroller.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 32)
        {
            availableWidth = Math.Max(1, ActualWidth);
        }

        ChapterList.Width = Math.Max(1, availableWidth);
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
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
    }

    private ChapterRenderRequest FullRequest =>
        _fullRequest ?? throw new InvalidOperationException("Reader render size is not configured.");

    private ChapterRenderRequest PreviousRequest =>
        _previousRequest ?? throw new InvalidOperationException("Reader render size is not configured.");

    private void UpdateProgress(ChapterLoadProgress progress)
    {
        if (_closed) return;

        LoadingProgress.Maximum = Math.Max(1, progress.Total);
        LoadingProgress.Value = progress.Loaded;
        LoadingDetail.Text = progress.Loaded == 0
            ? $"{progress.Stage} · {progress.Total} pages"
            : $"{progress.Stage} · {progress.Loaded} / {progress.Total}";
    }

    private void ShowError(string message)
    {
        LoadingTitle.Text = "Could not open chapter";
        LoadingDetail.Text = message;
        LoadingProgress.Visibility = Visibility.Collapsed;
        CloseAfterErrorButton.Visibility = Visibility.Visible;
    }

    private async void ReaderScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_zoomController?.IsApplying == true) return;
        await EvaluateActiveSurfaceAsync();
    }

    private void ReaderScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ChapterList.Width = Math.Max(1, e.NewSize.Width);
    }

    private void ReaderWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0
            && e.Key is Key.D0 or Key.NumPad0)
        {
            _zoomController?.Reset();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ReaderWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_zoomController?.HandleMouseWheel(e) == true) e.Handled = true;
    }

    private void UpdateWindowTitle() =>
        Title = $"{_title.Title} — {_title.Chapters[_activeChapterIndex].Title} — Manga Reader";

    private void CloseAfterErrorButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ReaderWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _zoomController?.Dispose();
        _zoomController = null;
        _loadCancellation.Cancel();
        ChapterList.ItemsSource = null;
        _surfaces.Clear();
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
