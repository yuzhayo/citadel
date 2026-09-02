using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Citadel.Setting.Components;

namespace Module.Mangareader;

/// <summary>Only window-level preview input owner for the Reader.</summary>
public sealed class ReaderInputRouter : IReaderInputEvents, IDisposable
{
    private readonly Window _window;
    private readonly IReaderViewport _viewport;
    private readonly IReaderStateView _state;
    private readonly IReaderCommands _commands;
    private readonly ReaderActivityHub _activity;
    private bool _pointerArmed;
    private Point _pointerDown;
    private bool _disposed;

    public ReaderInputRouter(
        Window window,
        IReaderViewport viewport,
        IReaderStateView state,
        IReaderCommands commands,
        ReaderActivityHub activity)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));

        _window.PreviewMouseLeftButtonDown += OnMouseDown;
        _window.PreviewMouseLeftButtonUp += OnMouseUp;
        _window.PreviewMouseWheel += OnMouseWheel;
        _window.PreviewKeyDown += OnKeyDown;
        _window.PreviewTouchDown += OnTouchDown;
        _window.Deactivated += OnWindowDeactivated;
    }

    public event EventHandler<ReaderOverlayClickEventArgs>? OverlayClicked;
    public event EventHandler<MouseWheelEventArgs>? MouseWheel;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pointerArmed = false;
        if (_state.IsLoading || _state.HasError || _state.IsTransitioning) return;
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;

        var point = _viewport.GetPointerPosition(e);
        if (!IsInsideViewport(point)) return;

        _activity.Report(ReaderActivityOrigin.ManualPointer);
        _pointerDown = point;
        _pointerArmed = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerArmed) return;
        _pointerArmed = false;

        var point = _viewport.GetPointerPosition(e);
        if (!IsInsideViewport(point)) return;
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (ReaderInputPolicy.ExceedsDragThreshold(
                point.X - _pointerDown.X,
                point.Y - _pointerDown.Y,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        var zone = ReaderInputPolicy.ResolveOverlayZone(point.X, _viewport.ViewportWidth);
        OverlayClicked?.Invoke(this, new ReaderOverlayClickEventArgs(zone));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        _activity.Report(ReaderActivityOrigin.ManualWheel);
        MouseWheel?.Invoke(this, e);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape)
        {
            switch (ReaderInputPolicy.ResolveEscape(_state))
            {
                case ReaderEscapeAction.ExitFullscreen:
                    _commands.ExitFullscreen();
                    break;
                case ReaderEscapeAction.CloseDrawer:
                    _commands.CloseDrawer();
                    break;
                default:
                    _commands.CloseReader();
                    break;
            }
            e.Handled = true;
            return;
        }

        switch (ReaderInputPolicy.ResolveKey(key, modifiers))
        {
            case ReaderKeyAction.ToggleFullscreen:
                _commands.ToggleFullscreen();
                e.Handled = true;
                break;
            case ReaderKeyAction.DimLighter:
                _commands.ChangeDim(-1);
                e.Handled = true;
                break;
            case ReaderKeyAction.DimDarker:
                _commands.ChangeDim(1);
                e.Handled = true;
                break;
            case ReaderKeyAction.ResetDim:
                _commands.ResetDim();
                e.Handled = true;
                break;
            case ReaderKeyAction.ResetZoom:
                _activity.Report(ReaderActivityOrigin.Zoom);
                _commands.ResetZoom();
                e.Handled = true;
                break;
            case ReaderKeyAction.ReportKeyboardScroll:
                if (!IsInteractiveSource(Keyboard.FocusedElement as DependencyObject))
                    _activity.Report(ReaderActivityOrigin.KeyboardScroll);
                break;
        }
    }

    private void OnTouchDown(object? sender, TouchEventArgs e)
    {
        if (!IsInteractiveSource(e.OriginalSource as DependencyObject))
            _activity.Report(ReaderActivityOrigin.ManualTouch);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) =>
        _activity.Report(ReaderActivityOrigin.WindowDeactivated);

    private bool IsInsideViewport(Point point) =>
        point.X >= 0
        && point.Y >= 0
        && point.X <= _viewport.ViewportWidth
        && point.Y <= _viewport.ViewportHeight;

    internal static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = ParentOf(current))
        {
            if (current is ButtonBase
                or TextBoxBase
                or Selector
                or RangeBase
                or Thumb
                or ScrollBar
                or SettingWindowChrome
                or SettingDrawer)
            {
                return true;
            }
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject current)
    {
        if (current is FrameworkContentElement contentElement)
            return contentElement.Parent;
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.PreviewMouseLeftButtonDown -= OnMouseDown;
        _window.PreviewMouseLeftButtonUp -= OnMouseUp;
        _window.PreviewMouseWheel -= OnMouseWheel;
        _window.PreviewKeyDown -= OnKeyDown;
        _window.PreviewTouchDown -= OnTouchDown;
        _window.Deactivated -= OnWindowDeactivated;
    }
}
