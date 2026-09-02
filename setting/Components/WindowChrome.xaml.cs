using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Citadel.Setting.Components;

/// <summary>Themed, overlay-only, auto-fading chrome for standalone WPF windows.</summary>
public sealed partial class SettingWindowChrome : UserControl
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);
    private const double TriggerStripHeight = 6;

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(SettingWindowChrome),
            new FrameworkPropertyMetadata(string.Empty));

    private Window? _window;
    private WindowChrome? _previousWindowChrome;
    private WindowChrome? _installedWindowChrome;
    private DependencyPropertyDescriptor? _resizeModeDescriptor;
    private DispatcherTimer? _idleTimer;
    private bool _attached;
    private bool _visible;
    private bool _systemActionActive;
    private int _animationGeneration;

    public SettingWindowChrome()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    internal bool HasActiveSession => _attached;
    internal bool IsChromeVisible => _visible;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _window = Window.GetWindow(this);
        if (_window is null) return;
        _attached = true;

        _previousWindowChrome = WindowChrome.GetWindowChrome(_window);
        _installedWindowChrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
            GlassFrameThickness = new Thickness(0),
        };
        WindowChrome.SetWindowChrome(_window, _installedWindowChrome);

        _window.StateChanged += OnWindowStateChanged;
        _window.MouseMove += OnWindowMouseMove;
        _resizeModeDescriptor = DependencyPropertyDescriptor.FromProperty(
            Window.ResizeModeProperty,
            typeof(Window));
        _resizeModeDescriptor?.AddValueChanged(_window, OnWindowResizeModeChanged);
        MinimizeButton.Click += OnMinimize;
        MaxRestoreButton.Click += OnMaxRestore;
        CloseButton.Click += OnClose;
        ChromeSurface.MouseEnter += OnChromeMouseEnter;
        ChromeSurface.MouseLeave += OnChromeMouseLeave;
        ChromeSurface.MouseLeftButtonDown += OnChromeMouseLeftButtonDown;
        IsKeyboardFocusWithinChanged += OnKeyboardFocusWithinChanged;
        IsMouseCaptureWithinChanged += OnMouseCaptureWithinChanged;

        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = IdleTimeout,
        };
        _idleTimer.Tick += OnIdleTick;

        SyncMaxRestoreIcon();
        SetOpacityImmediate(1);
        _visible = true;
        ScheduleHide();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _animationGeneration++;

        if (_idleTimer is not null)
        {
            _idleTimer.Stop();
            _idleTimer.Tick -= OnIdleTick;
            _idleTimer = null;
        }

        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window.MouseMove -= OnWindowMouseMove;
            _resizeModeDescriptor?.RemoveValueChanged(_window, OnWindowResizeModeChanged);
            if (ReferenceEquals(
                WindowChrome.GetWindowChrome(_window),
                _installedWindowChrome))
            {
                WindowChrome.SetWindowChrome(_window, _previousWindowChrome);
            }
        }

        MinimizeButton.Click -= OnMinimize;
        MaxRestoreButton.Click -= OnMaxRestore;
        CloseButton.Click -= OnClose;
        ChromeSurface.MouseEnter -= OnChromeMouseEnter;
        ChromeSurface.MouseLeave -= OnChromeMouseLeave;
        ChromeSurface.MouseLeftButtonDown -= OnChromeMouseLeftButtonDown;
        IsKeyboardFocusWithinChanged -= OnKeyboardFocusWithinChanged;
        IsMouseCaptureWithinChanged -= OnMouseCaptureWithinChanged;
        BeginAnimation(OpacityProperty, null);
        _systemActionActive = false;
        _previousWindowChrome = null;
        _installedWindowChrome = null;
        _resizeModeDescriptor = null;
        _window = null;
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        Reveal();
        if (_window is not null) _window.WindowState = WindowState.Minimized;
    }

    private void OnMaxRestore(object sender, RoutedEventArgs e)
    {
        Reveal();
        if (_window is null || !CanResize(_window.ResizeMode)) return;
        ToggleMaxRestore(_window);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Reveal();
        _window?.Close();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => SyncMaxRestoreIcon();

    private void OnWindowResizeModeChanged(object? sender, EventArgs e) => SyncMaxRestoreIcon();

    private void SyncMaxRestoreIcon()
    {
        if (_window is null) return;
        MaxRestoreButton.IsEnabled = CanResize(_window.ResizeMode);
        var icon = MaxRestoreButton.Content as Path ?? FindDescendant<Path>(MaxRestoreButton);
        if (icon is null) return;
        icon.Data = _window.WindowState == WindowState.Maximized
            ? System.Windows.Media.Geometry.Parse("M 2 0 L 10 0 L 10 8 M 0 2 L 8 2 L 8 10 L 0 10 Z")
            : System.Windows.Media.Geometry.Parse("M 0 0 L 10 0 L 10 10 L 0 10 Z");
        MaxRestoreButton.ToolTip = _window.WindowState == WindowState.Maximized
            ? "Restore"
            : "Maximise";
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnChromeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_window is null) return;
        Reveal();
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            if (CanResize(_window.ResizeMode)) ToggleMaxRestore(_window);
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;
        _systemActionActive = true;
        try
        {
            TryDragMove(_window.DragMove);
            e.Handled = true;
        }
        finally
        {
            _systemActionActive = false;
            ScheduleHide();
        }
    }

    internal static bool CanResize(ResizeMode resizeMode) =>
        resizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

    private static void ToggleMaxRestore(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    internal static bool TryDragMove(Action dragMove)
    {
        ArgumentNullException.ThrowIfNull(dragMove);
        try
        {
            dragMove();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (IsPointerInVisibleTopTrigger(e))
            Reveal();
    }

    private bool IsPointerInVisibleTopTrigger(MouseEventArgs e)
    {
        if (_window is null) return false;

        var pointerScreen = PointToScreen(e.GetPosition(this));
        var contentTopScreen = PointToScreen(new Point(0, 0)).Y;
        var monitorTopScreen = contentTopScreen;
        if (_window.WindowState == WindowState.Maximized)
        {
            var handle = new WindowInteropHelper(_window).Handle;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                monitorTopScreen = info.Monitor.Top;
        }

        return IsWithinVisibleTopTrigger(
            pointerScreen.Y,
            contentTopScreen,
            monitorTopScreen,
            _window.WindowState == WindowState.Maximized,
            VisualTreeHelper.GetDpi(this).DpiScaleY);
    }

    internal static bool IsWithinVisibleTopTrigger(
        double pointerScreenY,
        double contentTopScreenY,
        double monitorTopScreenY,
        bool maximized,
        double dpiScale)
    {
        if (!double.IsFinite(pointerScreenY)
            || !double.IsFinite(contentTopScreenY)
            || !double.IsFinite(monitorTopScreenY)
            || !double.IsFinite(dpiScale)
            || dpiScale <= 0)
        {
            return false;
        }

        var visibleTop = maximized
            ? Math.Max(contentTopScreenY, monitorTopScreenY)
            : contentTopScreenY;
        return pointerScreenY >= visibleTop
            && pointerScreenY <= visibleTop + (TriggerStripHeight * dpiScale);
    }

    private void OnChromeMouseEnter(object sender, MouseEventArgs e) => Reveal();

    private void OnChromeMouseLeave(object sender, MouseEventArgs e) => ScheduleHide();

    private void OnKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsKeyboardFocusWithin) Reveal();
        else ScheduleHide();
    }

    private void OnMouseCaptureWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsMouseCaptureWithin) Reveal();
        else ScheduleHide();
    }

    private bool IsHeld => ShouldHoldVisibility(
        IsMouseOver,
        IsKeyboardFocusWithin,
        IsMouseCaptureWithin,
        _systemActionActive);

    internal static bool ShouldHoldVisibility(
        bool isMouseOver,
        bool isKeyboardFocusWithin,
        bool isMouseCaptureWithin,
        bool isSystemActionActive) =>
        isMouseOver || isKeyboardFocusWithin || isMouseCaptureWithin || isSystemActionActive;

    private void Reveal()
    {
        if (!_attached) return;
        _idleTimer?.Stop();
        if (!_visible)
        {
            _visible = true;
            IsHitTestVisible = true;
            AnimateTo(1);
        }
        if (!IsHeld) _idleTimer?.Start();
    }

    private void ScheduleHide()
    {
        if (!_attached || !_visible || IsHeld) return;
        _idleTimer?.Stop();
        _idleTimer?.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        _idleTimer?.Stop();
        if (IsHeld) return;
        _visible = false;
        AnimateTo(0);
    }

    private void AnimateTo(double target)
    {
        var generation = ++_animationGeneration;
        if (!SystemParameters.ClientAreaAnimation)
        {
            SetOpacityImmediate(target);
            return;
        }

        var current = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = current;
        var animation = new DoubleAnimation(current, target, FadeDuration)
        {
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            if (generation == _animationGeneration) SetOpacityImmediate(target);
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private void SetOpacityImmediate(double value)
    {
        _animationGeneration++;
        BeginAnimation(OpacityProperty, null);
        Opacity = value;
        IsHitTestVisible = value > 0.001;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
