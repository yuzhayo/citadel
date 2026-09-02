using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public interface IReaderFeature : IDisposable
{
    string FeatureName { get; }
    void Attach(ReaderFeatureContext context);
}

public interface IReaderStartableFeature
{
    Task StartAsync();
}

public interface IReaderVisualFeature
{
    IReadOnlyList<ReaderVisualContribution> Visuals { get; }
}

public sealed record ReaderVisualContribution(ReaderLayer Layer, FrameworkElement View);

public interface IReaderDrawerContributionProvider
{
    IReadOnlyList<ReaderDrawerContribution> DrawerContributions { get; }
}

public interface IReaderDrawerContributionHost
{
    void SetContributions(IReadOnlyList<ReaderDrawerContribution> contributions);
}

public abstract class ReaderDrawerContribution
{
    protected ReaderDrawerContribution(string key, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Order = order;
    }

    public string Key { get; }
    public int Order { get; }
}

public interface IReaderCommands
{
    void ToggleDrawer();
    void CloseDrawer();
    void ToggleFullscreen();
    void ExitFullscreen();
    void CloseReader();
    void TogglePin();
    void ChangeZoom(int steps);
    void ResetZoom(ReaderActivityOrigin origin = ReaderActivityOrigin.Zoom);
    void SetDim(double percent);
    void ChangeDim(int steps);
    void ResetDim();
    void StartAutoScroll();
    void SetAutoScrollSpeed(double secondsPerViewport);
    void StopAutoScroll();
    void ResetAll();
}

internal sealed class ReaderCommandHub : IReaderCommands
{
    internal event Action? ToggleDrawerRequested;
    internal event Action? CloseDrawerRequested;
    internal event Action? ToggleFullscreenRequested;
    internal event Action? ExitFullscreenRequested;
    internal event Action? CloseReaderRequested;
    internal event Action? TogglePinRequested;
    internal event Action<int>? ChangeZoomRequested;
    internal event Action<ReaderActivityOrigin>? ResetZoomRequested;
    internal event Action<double>? SetDimRequested;
    internal event Action<int>? ChangeDimRequested;
    internal event Action? ResetDimRequested;
    internal event Action? StartAutoScrollRequested;
    internal event Action<double>? SetAutoScrollSpeedRequested;
    internal event Action? StopAutoScrollRequested;
    internal event Action? ResetAllRequested;

    public void ToggleDrawer() => ToggleDrawerRequested?.Invoke();
    public void CloseDrawer() => CloseDrawerRequested?.Invoke();
    public void ToggleFullscreen() => ToggleFullscreenRequested?.Invoke();
    public void ExitFullscreen() => ExitFullscreenRequested?.Invoke();
    public void CloseReader() => CloseReaderRequested?.Invoke();
    public void TogglePin() => TogglePinRequested?.Invoke();
    public void ChangeZoom(int steps) => ChangeZoomRequested?.Invoke(steps);
    public void ResetZoom(ReaderActivityOrigin origin = ReaderActivityOrigin.Zoom) =>
        ResetZoomRequested?.Invoke(origin);
    public void SetDim(double percent) => SetDimRequested?.Invoke(percent);
    public void ChangeDim(int steps) => ChangeDimRequested?.Invoke(steps);
    public void ResetDim() => ResetDimRequested?.Invoke();
    public void StartAutoScroll() => StartAutoScrollRequested?.Invoke();
    public void SetAutoScrollSpeed(double secondsPerViewport) =>
        SetAutoScrollSpeedRequested?.Invoke(secondsPerViewport);
    public void StopAutoScroll() => StopAutoScrollRequested?.Invoke();
    public void ResetAll() => ResetAllRequested?.Invoke();
}

public sealed class ReaderToastRequestEventArgs(string message, TimeSpan duration) : EventArgs
{
    public string Message { get; } = message;
    public TimeSpan Duration { get; } = duration;
}

public sealed class ReaderNotificationHub
{
    public event EventHandler<ReaderToastRequestEventArgs>? ToastRequested;

    public void ShowToast(string message, TimeSpan? duration = null) =>
        ToastRequested?.Invoke(
            this,
            new ReaderToastRequestEventArgs(
                message ?? string.Empty,
                duration ?? TimeSpan.FromSeconds(2)));
}

public sealed class ReaderViewportChangedEventArgs(
    ReaderActivityOrigin origin,
    double verticalChange,
    double horizontalChange) : EventArgs
{
    public ReaderActivityOrigin Origin { get; } = origin;
    public double VerticalChange { get; } = verticalChange;
    public double HorizontalChange { get; } = horizontalChange;
}

public interface IReaderViewport : IDisposable
{
    event EventHandler<ReaderViewportChangedEventArgs>? Changed;
    event EventHandler? SizeChanged;

    FrameworkElement InputElement { get; }
    Dispatcher Dispatcher { get; }
    double VerticalOffset { get; }
    double HorizontalOffset { get; }
    double ViewportHeight { get; }
    double ViewportWidth { get; }
    double ExtentHeight { get; }
    double ExtentWidth { get; }
    double ScrollableHeight { get; }
    double ScrollableWidth { get; }
    double DpiScale { get; }

    void SetContentWidth(double width);
    void UpdateLayout();
    void ScrollToVerticalOffset(double offset, ReaderActivityOrigin origin);
    void ScrollToHorizontalOffset(double offset, ReaderActivityOrigin origin);
    FrameworkElement? ItemContainerFor(object item);
    Point GetPointerPosition(MouseEventArgs args);
}

public interface IReaderStatusHost
{
    void ShowLoading(string title, string detail);
    void ReportProgress(ChapterLoadProgress progress);
    void Hide();
    void ShowError(string message);
    void SetNonBlockingDetail(string message);
}

public interface IReaderChapterLoader
{
    Task<LoadedChapter> LoadAsync(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IReaderChapterNavigation
{
    IReadOnlyList<ChapterInfo> Chapters { get; }
    ReadOnlyObservableCollection<ChapterSurfaceModel> Surfaces { get; }
    int ActiveChapterIndex { get; }
    ChapterInfo ActiveChapter { get; }
    string MangaTitle { get; }
    bool CanNavigatePrevious { get; }
    bool CanNavigateNext { get; }
    bool IsAtAbsoluteBeginning { get; }
    bool IsAtAbsoluteEnd { get; }

    event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    Task NavigateToChapterAsync(int index);
    Task PrepareBoundaryAsync(int direction, CancellationToken cancellationToken);
    void NotifyZoomChanged();
}

public interface IReaderChapterNavigationRegistry
{
    void Register(IReaderChapterNavigation implementation);
    void Unregister(IReaderChapterNavigation implementation);
}

public sealed class ReaderContentContext
{
    public ReaderContentContext(
        MangaTitle title,
        ChapterInfo initialChapter,
        IReaderChapterLoader chapterLoader,
        IReaderStatusHost status)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        InitialChapter = initialChapter ?? throw new ArgumentNullException(nameof(initialChapter));
        ChapterLoader = chapterLoader ?? throw new ArgumentNullException(nameof(chapterLoader));
        Status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public MangaTitle Title { get; }
    public ChapterInfo InitialChapter { get; }
    public IReaderChapterLoader ChapterLoader { get; }
    public IReaderStatusHost Status { get; }
}

public enum ReaderOverlayZone
{
    Previous,
    Menu,
    Next,
}

public sealed class ReaderOverlayClickEventArgs(ReaderOverlayZone zone) : EventArgs
{
    public ReaderOverlayZone Zone { get; } = zone;
}

public interface IReaderInputEvents
{
    event EventHandler<ReaderOverlayClickEventArgs>? OverlayClicked;
    event EventHandler<MouseWheelEventArgs>? MouseWheel;
}

public sealed class ReaderFeatureContext
{
    internal ReaderFeatureContext(
        ReaderSessionState state,
        IReaderCommands commands,
        IReaderViewport viewport,
        IReaderChapterNavigation chapters,
        IReaderChapterNavigationRegistry chapterNavigationRegistry,
        ReaderContentContext content,
        IReaderInputEvents input,
        ReaderActivityHub activity,
        ReaderNotificationHub notifications,
        CancellationToken lifetime)
    {
        SessionState = state;
        State = state;
        Commands = commands;
        Viewport = viewport;
        Chapters = chapters;
        ChapterNavigationRegistry = chapterNavigationRegistry;
        Content = content;
        Input = input;
        Activity = activity;
        Notifications = notifications;
        Lifetime = lifetime;
    }

    public IReaderStateView State { get; }
    internal ReaderSessionState SessionState { get; }
    public IReaderCommands Commands { get; }
    public IReaderViewport Viewport { get; }
    public IReaderChapterNavigation Chapters { get; }
    public IReaderChapterNavigationRegistry ChapterNavigationRegistry { get; }
    public ReaderContentContext Content { get; }
    public IReaderInputEvents Input { get; }
    public ReaderActivityHub Activity { get; }
    public ReaderNotificationHub Notifications { get; }
    public Dispatcher Dispatcher => Viewport.Dispatcher;
    public CancellationToken Lifetime { get; }
}
