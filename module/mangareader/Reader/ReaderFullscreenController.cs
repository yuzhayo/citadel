using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Citadel.Setting.Components;

namespace Module.Mangareader;

public sealed class ReaderFullscreenController : IReaderFeature, IReaderDrawerContributionProvider
{
    private readonly Window _window;
    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly SettingButton _action;
    private readonly ReaderDrawerCardContribution _contribution;
    private ReaderFeatureContext? _context;
    private WindowSnapshot? _snapshot;
    private bool _disposed;

    internal ReaderFullscreenController(
        Window window,
        ReaderSessionState state,
        ReaderCommandHub commands)
    {
        _window = window;
        _state = state;
        _commands = commands;
        _action = new SettingButton
        {
            Content = "Enter",
            ToolTip = "Toggle fullscreen (F11)",
        };
        AutomationProperties.SetName(_action, "Toggle fullscreen");
        AutomationProperties.SetAutomationId(_action, "ReaderFullscreenAction");
        _action.Click += OnActionClick;
        _contribution = new ReaderDrawerCardContribution(
            "fullscreen",
            200,
            new SettingActionCard
            {
                Content = ReaderDrawerCards.Label("Fullscreen"),
                ActionContent = _action,
            });
    }

    public string FeatureName => "Fullscreen";
    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions => [_contribution];

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        _commands.ToggleFullscreenRequested += Toggle;
        _commands.ExitFullscreenRequested += Exit;
        _state.PropertyChanged += OnStateChanged;
        UpdateLabel();
    }

    private void Toggle()
    {
        if (_state.IsFullscreen) Exit();
        else Enter();
    }

    private void Enter()
    {
        if (_state.IsFullscreen || _disposed || _context is null) return;

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new ReaderMonitorInfoEx { Size = Marshal.SizeOf<ReaderMonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var source = PresentationSource.FromVisual(_window);
        if (source?.CompositionTarget is null) return;

        Rect bounds;
        try
        {
            bounds = ReaderFullscreenGeometry.ToDipRect(
                new ReaderPixelRect(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right,
                    info.Monitor.Bottom),
                source.CompositionTarget.TransformFromDevice);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        var restoreBounds = _window.RestoreBounds;
        if (restoreBounds.IsEmpty || restoreBounds.Width <= 0 || restoreBounds.Height <= 0)
        {
            restoreBounds = new Rect(
                _window.Left,
                _window.Top,
                _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width,
                _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height);
        }

        var snapshot = new WindowSnapshot(
            _window.WindowState,
            _window.WindowStyle,
            _window.ResizeMode,
            _window.Topmost,
            restoreBounds);

        try
        {
            _window.WindowState = WindowState.Normal;
            _window.WindowStyle = WindowStyle.None;
            _window.ResizeMode = ResizeMode.NoResize;
            _window.Topmost = false;
            _window.Left = bounds.Left;
            _window.Top = bounds.Top;
            _window.Width = bounds.Width;
            _window.Height = bounds.Height;
            _snapshot = snapshot;
            _state.SetFullscreen(true);
            _context.Notifications.ShowToast("To exit full screen, press Esc");
        }
        catch
        {
            Restore(snapshot);
            _snapshot = null;
            _state.SetFullscreen(false);
        }
    }

    private void Exit()
    {
        if (!_state.IsFullscreen || _snapshot is not { } snapshot) return;
        Restore(snapshot);
        _snapshot = null;
        _state.SetFullscreen(false);
    }

    private void Restore(WindowSnapshot snapshot)
    {
        _window.WindowState = WindowState.Normal;
        _window.WindowStyle = snapshot.Style;
        _window.ResizeMode = snapshot.ResizeMode;
        _window.Topmost = snapshot.Topmost;
        _window.Left = snapshot.RestoreBounds.Left;
        _window.Top = snapshot.RestoreBounds.Top;
        _window.Width = snapshot.RestoreBounds.Width;
        _window.Height = snapshot.RestoreBounds.Height;
        _window.WindowState = snapshot.State;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IReaderStateView.IsFullscreen)) UpdateLabel();
    }

    private void OnActionClick(object sender, RoutedEventArgs e) => _commands.ToggleFullscreen();

    private void UpdateLabel() =>
        _action.Content = _state.IsFullscreen ? "Exit" : "Enter";

    public void Dispose()
    {
        if (_disposed) return;
        Exit();
        _disposed = true;
        _commands.ToggleFullscreenRequested -= Toggle;
        _commands.ExitFullscreenRequested -= Exit;
        _state.PropertyChanged -= OnStateChanged;
        _action.Click -= OnActionClick;
        _context = null;
    }

    private const uint MonitorDefaultToNearest = 2;

    private sealed record WindowSnapshot(
        WindowState State,
        WindowStyle Style,
        ResizeMode ResizeMode,
        bool Topmost,
        Rect RestoreBounds);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref ReaderMonitorInfoEx info);
}
