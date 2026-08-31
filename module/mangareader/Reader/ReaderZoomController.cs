using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Module.Mangareader;

internal sealed class ReaderZoomController : IDisposable
{
    public const double DefaultScale = 1;
    public const double MinimumScale = 0.5;
    public const double MaximumScale = 3;
    public const double ScaleStep = 0.1;

    private readonly ScrollViewer _scroller;
    private readonly Func<double> _readScale;
    private readonly Action<double> _writeScale;
    private readonly DispatcherTimer _applyTimer;

    private Point _anchor;
    private double _targetScale;
    private bool _disposed;

    public ReaderZoomController(
        ScrollViewer scroller,
        Func<double> readScale,
        Action<double> writeScale)
    {
        _scroller = scroller ?? throw new ArgumentNullException(nameof(scroller));
        _readScale = readScale ?? throw new ArgumentNullException(nameof(readScale));
        _writeScale = writeScale ?? throw new ArgumentNullException(nameof(writeScale));
        _targetScale = readScale();
        _applyTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            ApplyPendingZoom,
            scroller.Dispatcher);
        _applyTimer.Stop();
    }

    public bool IsApplying { get; private set; }

    public bool HandleMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_disposed || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;

        _anchor = e.GetPosition(_scroller);
        var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        var direction = Math.Sign(e.Delta);
        _targetScale = ClampAndRound(
            _targetScale + (direction * ScaleStep * notches));
        QueueApply();
        return true;
    }

    public void Reset()
    {
        if (_disposed) return;

        _anchor = new Point(
            _scroller.ViewportWidth / 2,
            _scroller.ViewportHeight / 2);
        _targetScale = DefaultScale;
        QueueApply();
    }

    private void QueueApply()
    {
        if (!_applyTimer.IsEnabled) _applyTimer.Start();
    }

    private void ApplyPendingZoom(object? sender, EventArgs e)
    {
        _applyTimer.Stop();
        if (_disposed) return;

        var oldScale = _readScale();
        var newScale = _targetScale;
        if (Math.Abs(newScale - oldScale) < 0.001) return;

        var anchorX = Math.Clamp(_anchor.X, 0, Math.Max(0, _scroller.ViewportWidth));
        var anchorY = Math.Clamp(_anchor.Y, 0, Math.Max(0, _scroller.ViewportHeight));
        var contentX = (_scroller.HorizontalOffset + anchorX) / oldScale;
        var contentY = (_scroller.VerticalOffset + anchorY) / oldScale;

        IsApplying = true;
        try
        {
            _writeScale(newScale);
            _scroller.UpdateLayout();
            _scroller.ScrollToHorizontalOffset(Math.Max(0, (contentX * newScale) - anchorX));
            _scroller.ScrollToVerticalOffset(Math.Max(0, (contentY * newScale) - anchorY));
        }
        finally
        {
            IsApplying = false;
        }
    }

    private static double ClampAndRound(double scale) =>
        Math.Round(
            Math.Clamp(scale, MinimumScale, MaximumScale),
            1,
            MidpointRounding.AwayFromZero);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _applyTimer.Stop();
        _applyTimer.Tick -= ApplyPendingZoom;
    }
}
