using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

internal static class WpfTest
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public static void PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }

        Assert.True(condition(), "Condition did not become true before timeout.");
    }

    public static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

internal sealed class TestViewport : IReaderViewport
{
    private readonly Border _input = new();
    private double _verticalOffset;
    private double _horizontalOffset;

    public event EventHandler<ReaderViewportChangedEventArgs>? Changed;
    public event EventHandler? SizeChanged;

    public FrameworkElement InputElement => _input;
    public Dispatcher Dispatcher => _input.Dispatcher;
    public double VerticalOffset => _verticalOffset;
    public double HorizontalOffset => _horizontalOffset;
    public double ViewportHeight { get; set; } = 600;
    public double ViewportWidth { get; set; } = 800;
    public double ExtentHeight { get; set; } = 3000;
    public double ExtentWidth { get; set; } = 800;
    public double ScrollableHeight { get; set; } = 2400;
    public double ScrollableWidth { get; set; }
    public double DpiScale { get; set; } = 1;
    public double ContentWidth { get; private set; }
    public bool RaiseLayoutChangedReentrantly { get; set; }
    public Point PointerPosition { get; set; } = new(400, 300);
    public List<(double Offset, ReaderActivityOrigin Origin)> VerticalScrolls { get; } = [];
    public List<(double Offset, ReaderActivityOrigin Origin)> HorizontalScrolls { get; } = [];

    public void SetContentWidth(double width) => ContentWidth = width;
    public void UpdateLayout()
    {
        if (!RaiseLayoutChangedReentrantly) return;
        Changed?.Invoke(
            this,
            new ReaderViewportChangedEventArgs(
                ReaderActivityOrigin.ManualScroll,
                0,
                0));
        WpfTest.PumpFor(TimeSpan.FromMilliseconds(5));
    }

    public void ScrollToVerticalOffset(double offset, ReaderActivityOrigin origin)
    {
        var previous = _verticalOffset;
        _verticalOffset = Math.Clamp(offset, 0, Math.Max(0, ScrollableHeight));
        VerticalScrolls.Add((_verticalOffset, origin));
        var change = _verticalOffset - previous;
        if (Math.Abs(change) > 0.001)
            Changed?.Invoke(this, new ReaderViewportChangedEventArgs(origin, change, 0));
    }

    public void ScrollToHorizontalOffset(double offset, ReaderActivityOrigin origin)
    {
        var previous = _horizontalOffset;
        _horizontalOffset = Math.Clamp(offset, 0, Math.Max(0, ScrollableWidth));
        HorizontalScrolls.Add((_horizontalOffset, origin));
        var change = _horizontalOffset - previous;
        if (Math.Abs(change) > 0.001)
            Changed?.Invoke(this, new ReaderViewportChangedEventArgs(origin, 0, change));
    }

    public FrameworkElement? ItemContainerFor(object item) => null;
    public Point GetPointerPosition(MouseEventArgs args) => PointerPosition;
    public void RaiseSizeChanged() => SizeChanged?.Invoke(this, EventArgs.Empty);
    public void Dispose() { }
}

internal sealed class TestStatusHost : IReaderStatusHost
{
    public bool IsVisible { get; private set; }
    public bool HasError { get; private set; }
    public int HideCount { get; private set; }
    public string? Detail { get; private set; }
    public List<ChapterLoadProgress> Progress { get; } = [];

    public void ShowLoading(string title, string detail)
    {
        IsVisible = true;
        HasError = false;
        Detail = detail;
    }

    public void ReportProgress(ChapterLoadProgress progress) => Progress.Add(progress);

    public void Hide()
    {
        IsVisible = false;
        HideCount++;
    }

    public void ShowError(string message)
    {
        IsVisible = true;
        HasError = true;
        Detail = message;
    }

    public void SetNonBlockingDetail(string message) => Detail = message;
}

internal sealed class TestInputEvents : IReaderInputEvents
{
    public event EventHandler<ReaderOverlayClickEventArgs>? OverlayClicked;
    public event EventHandler<MouseWheelEventArgs>? MouseWheel;

    public void Click(ReaderOverlayZone zone) =>
        OverlayClicked?.Invoke(this, new ReaderOverlayClickEventArgs(zone));

    public void Wheel(MouseWheelEventArgs args) => MouseWheel?.Invoke(this, args);
}

internal sealed class TestChapterNavigation : IReaderChapterNavigation
{
    private readonly ObservableCollection<ChapterSurfaceModel> _surfaces = [];
    private readonly ReadOnlyObservableCollection<ChapterSurfaceModel> _readOnlySurfaces;

    public TestChapterNavigation(IReadOnlyList<ChapterInfo>? chapters = null)
    {
        Chapters = chapters ?? [new ChapterInfo("Chapter 1", "1.cbz")];
        _readOnlySurfaces = new ReadOnlyObservableCollection<ChapterSurfaceModel>(_surfaces);
    }

    public IReadOnlyList<ChapterInfo> Chapters { get; }
    public ReadOnlyObservableCollection<ChapterSurfaceModel> Surfaces => _readOnlySurfaces;
    public int ActiveChapterIndex { get; set; }
    public ChapterInfo ActiveChapter => Chapters[ActiveChapterIndex];
    public string MangaTitle => "Test title";
    public bool CanNavigatePrevious => ActiveChapterIndex > 0;
    public bool CanNavigateNext => ActiveChapterIndex + 1 < Chapters.Count;
    public bool IsAtAbsoluteBeginning { get; set; }
    public bool IsAtAbsoluteEnd { get; set; }
    public int ZoomNotifications { get; private set; }
    public List<int> NavigationRequests { get; } = [];
    public List<int> BoundaryRequests { get; } = [];
    public Func<int, Task>? NavigationHandler { get; set; }
    public Func<int, CancellationToken, Task>? BoundaryHandler { get; set; }

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    public Task NavigateToChapterAsync(int index)
    {
        NavigationRequests.Add(index);
        if (NavigationHandler is not null) return NavigationHandler(index);
        ActiveChapterIndex = index;
        ActiveChapterChanged?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(
                new MangaTitle(MangaTitle, string.Empty, Chapters),
                ActiveChapter));
        return Task.CompletedTask;
    }

    public Task PrepareBoundaryAsync(int direction, CancellationToken cancellationToken)
    {
        BoundaryRequests.Add(direction);
        return BoundaryHandler?.Invoke(direction, cancellationToken) ?? Task.CompletedTask;
    }

    public void NotifyZoomChanged() => ZoomNotifications++;
}

internal sealed class DelegateChapterLoader(
    Func<ChapterInfo, ChapterRenderRequest, CancellationToken, Task<LoadedChapter>> load)
    : IReaderChapterLoader
{
    public List<(ChapterInfo Chapter, ChapterRenderRequest Request)> Calls { get; } = [];

    public Task<LoadedChapter> LoadAsync(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Calls.Add((chapter, request));
        return load(chapter, request, cancellationToken);
    }

    public static LoadedChapter Chapter(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        double height = 1000) =>
        new(chapter, [], 800, height, 0, request.Quality);
}

internal static class ReaderTestContext
{
    public static ReaderFeatureContext Create(
        ReaderSessionState? state = null,
        ReaderCommandHub? commands = null,
        TestViewport? viewport = null,
        IReaderChapterNavigation? chapters = null,
        TestInputEvents? input = null,
        ReaderActivityHub? activity = null,
        ReaderNotificationHub? notifications = null,
        CancellationToken lifetime = default) =>
        new(
            state ?? new ReaderSessionState(),
            commands ?? new ReaderCommandHub(),
            viewport ?? new TestViewport(),
            chapters ?? new TestChapterNavigation(),
            input ?? new TestInputEvents(),
            activity ?? new ReaderActivityHub(),
            notifications ?? new ReaderNotificationHub(),
            lifetime);

    public static MangaTitle Title(int count) =>
        new(
            "Test title",
            "C:\\test",
            Enumerable.Range(0, count)
                .Select(index => new ChapterInfo($"Chapter {index + 1}", $"{index}.cbz"))
                .ToArray());
}
