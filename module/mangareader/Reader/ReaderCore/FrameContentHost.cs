using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.ReaderCore;

/// <summary>
/// The only adapter between Reader policies and the concrete ScrollViewer/
/// ItemsControl pair. Every programmatic offset carries an activity origin.
/// </summary>
public sealed class FrameContentHost : IReaderViewport
{
    private readonly ScrollViewer _scroller;
    private readonly ItemsControl _chapterList;
    private readonly ReaderActivityHub _activity;
    private ReaderActivityOrigin? _pendingOrigin;
    private long _originGeneration;
    private bool _disposed;

    public FrameContentHost(
        ScrollViewer scroller,
        ItemsControl chapterList,
        ReaderActivityHub activity)
    {
        _scroller = scroller ?? throw new ArgumentNullException(nameof(scroller));
        _chapterList = chapterList ?? throw new ArgumentNullException(nameof(chapterList));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));

        _scroller.ScrollChanged += OnScrollChanged;
        _scroller.SizeChanged += OnSizeChanged;
    }

    public event EventHandler<ReaderViewportChangedEventArgs>? Changed;
    public event EventHandler? SizeChanged;

    public FrameworkElement InputElement => _scroller;
    public Dispatcher Dispatcher => _scroller.Dispatcher;
    public double VerticalOffset => _scroller.VerticalOffset;
    public double HorizontalOffset => _scroller.HorizontalOffset;
    public double ViewportHeight => _scroller.ViewportHeight;
    public double ViewportWidth => _scroller.ViewportWidth;
    public double ExtentHeight => _scroller.ExtentHeight;
    public double ExtentWidth => _scroller.ExtentWidth;
    public double ScrollableHeight => _scroller.ScrollableHeight;
    public double ScrollableWidth => _scroller.ScrollableWidth;
    public double DpiScale => VisualTreeHelper.GetDpi(_scroller).DpiScaleX;

    public void SetContentWidth(double width) =>
        _chapterList.Width = Math.Max(1, double.IsFinite(width) ? width : 1);

    public void UpdateLayout() => _scroller.UpdateLayout();

    public void ScrollToVerticalOffset(double offset, ReaderActivityOrigin origin)
    {
        MarkProgrammatic(origin);
        _scroller.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, ScrollableHeight)));
    }

    public void ScrollToHorizontalOffset(double offset, ReaderActivityOrigin origin)
    {
        MarkProgrammatic(origin);
        _scroller.ScrollToHorizontalOffset(Math.Clamp(offset, 0, Math.Max(0, ScrollableWidth)));
    }

    public FrameworkElement? ItemContainerFor(object item) =>
        _chapterList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

    public Point GetPointerPosition(MouseEventArgs args) => args.GetPosition(_scroller);

    private void MarkProgrammatic(ReaderActivityOrigin origin)
    {
        _pendingOrigin = origin;
        var generation = ++_originGeneration;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                if (_originGeneration == generation) _pendingOrigin = null;
            }));
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_disposed || (Math.Abs(e.VerticalChange) < 0.001 && Math.Abs(e.HorizontalChange) < 0.001))
            return;

        var origin = _pendingOrigin ?? ReaderActivityOrigin.ManualScroll;
        _pendingOrigin = null;
        _activity.Report(origin);
        Changed?.Invoke(
            this,
            new ReaderViewportChangedEventArgs(origin, e.VerticalChange, e.HorizontalChange));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_disposed) SizeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scroller.ScrollChanged -= OnScrollChanged;
        _scroller.SizeChanged -= OnSizeChanged;
    }
}
