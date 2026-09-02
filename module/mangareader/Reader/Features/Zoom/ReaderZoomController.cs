using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Citadel.Setting.Components;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>One zoom owner for Ctrl+wheel, Ctrl+0, and Drawer controls.</summary>
public sealed class ReaderZoomController : IReaderFeature, IReaderDrawerContributionProvider
{
    public const double DefaultScale = ReaderValuePolicy.DefaultZoomScale;
    public const double MinimumScale = ReaderValuePolicy.MinimumZoomScale;
    public const double MaximumScale = ReaderValuePolicy.MaximumZoomScale;
    public const double ScaleStep = ReaderValuePolicy.ZoomStep;

    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly TextBlock _valueText;
    private readonly SettingButton _decrease;
    private readonly SettingButton _increase;
    private readonly ReaderDrawerCardContribution _contribution;
    private ReaderFeatureContext? _context;
    private DispatcherTimer? _applyTimer;
    private Point _anchor;
    private double _targetScale = DefaultScale;
    private ReaderActivityOrigin _pendingOrigin = ReaderActivityOrigin.Zoom;
    private bool _disposed;

    internal ReaderZoomController(ReaderSessionState state, ReaderCommandHub commands)
    {
        _state = state;
        _commands = commands;
        _targetScale = state.ZoomScale;
        _valueText = ReaderDrawerCards.Label(Format(state.ZoomScale));
        _valueText.HorizontalAlignment = HorizontalAlignment.Center;
        _valueText.VerticalAlignment = VerticalAlignment.Center;

        _decrease = new SettingButton
        {
            Content = "−",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 4, 0),
        };
        AutomationProperties.SetName(_decrease, "Zoom out");
        AutomationProperties.SetAutomationId(_decrease, "ReaderZoomOut");

        _increase = new SettingButton
        {
            Content = "+",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 0, 0, 0),
        };
        AutomationProperties.SetName(_increase, "Zoom in");
        AutomationProperties.SetAutomationId(_increase, "ReaderZoomIn");

        var controls = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_decrease, 0);
        Grid.SetColumn(_valueText, 1);
        Grid.SetColumn(_increase, 2);
        controls.Children.Add(_decrease);
        controls.Children.Add(_valueText);
        controls.Children.Add(_increase);

        var content = new StackPanel();
        content.Children.Add(ReaderDrawerCards.Label("Zoom"));
        content.Children.Add(controls);
        _contribution = new ReaderDrawerCardContribution(
            "zoom",
            500,
            ReaderDrawerCards.Create(content, "ReaderZoomCard"));
        _decrease.Click += OnDecreaseClick;
        _increase.Click += OnIncreaseClick;
    }

    public string FeatureName => "Zoom";
    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions => [_contribution];

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        _applyTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            ApplyPendingZoom,
            context.Dispatcher);
        _applyTimer.Stop();

        context.Input.MouseWheel += OnMouseWheel;
        _commands.ChangeZoomRequested += OnChangeZoom;
        _commands.ResetZoomRequested += OnResetZoom;
        _state.PropertyChanged += OnStateChanged;
    }

    private void OnMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _context is null) return;
        _anchor = _context.Viewport.GetPointerPosition(e);
        var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        QueueScale(
            _targetScale + (Math.Sign(e.Delta) * ScaleStep * notches),
            ReaderActivityOrigin.Zoom);
        e.Handled = true;
    }

    private void OnChangeZoom(int steps)
    {
        if (_context is null) return;
        _anchor = new Point(
            _context.Viewport.ViewportWidth / 2,
            _context.Viewport.ViewportHeight / 2);
        QueueScale(_targetScale + (ScaleStep * steps), ReaderActivityOrigin.Zoom);
    }

    private void OnDecreaseClick(object sender, RoutedEventArgs e) => _commands.ChangeZoom(-1);

    private void OnIncreaseClick(object sender, RoutedEventArgs e) => _commands.ChangeZoom(1);

    private void OnResetZoom(ReaderActivityOrigin origin)
    {
        if (_context is null) return;
        _anchor = new Point(
            _context.Viewport.ViewportWidth / 2,
            _context.Viewport.ViewportHeight / 2);
        _targetScale = DefaultScale;
        _pendingOrigin = origin;
        _applyTimer?.Stop();
        ApplyScale(origin);
    }

    private void QueueScale(double value, ReaderActivityOrigin origin)
    {
        if (_disposed || _applyTimer is null) return;
        _targetScale = ReaderSessionState.NormalizeZoom(value);
        _pendingOrigin = origin;
        if (!_applyTimer.IsEnabled) _applyTimer.Start();
    }

    private void ApplyPendingZoom(object? sender, EventArgs e)
    {
        _applyTimer?.Stop();
        ApplyScale(_pendingOrigin);
    }

    private void ApplyScale(ReaderActivityOrigin origin)
    {
        if (_disposed || _context is null) return;

        var oldScale = _state.ZoomScale;
        var newScale = _targetScale;
        if (Math.Abs(newScale - oldScale) < 0.001) return;

        var viewport = _context.Viewport;
        var anchorX = Math.Clamp(_anchor.X, 0, Math.Max(0, viewport.ViewportWidth));
        var anchorY = Math.Clamp(_anchor.Y, 0, Math.Max(0, viewport.ViewportHeight));
        var contentX = (viewport.HorizontalOffset + anchorX) / oldScale;
        var contentY = (viewport.VerticalOffset + anchorY) / oldScale;

        _context.Activity.Report(origin);
        _state.SetZoomScale(newScale);
        viewport.UpdateLayout();
        viewport.ScrollToHorizontalOffset(
            Math.Max(0, (contentX * newScale) - anchorX),
            origin);
        viewport.ScrollToVerticalOffset(
            Math.Max(0, (contentY * newScale) - anchorY),
            origin);
        _context.Chapters.NotifyZoomChanged();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IReaderStateView.ZoomScale))
            _valueText.Text = Format(_state.ZoomScale);
    }

    private static string Format(double scale) => $"{Math.Round(scale * 100)}%";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context is not null) _context.Input.MouseWheel -= OnMouseWheel;
        _commands.ChangeZoomRequested -= OnChangeZoom;
        _commands.ResetZoomRequested -= OnResetZoom;
        _state.PropertyChanged -= OnStateChanged;
        _decrease.Click -= OnDecreaseClick;
        _increase.Click -= OnIncreaseClick;
        if (_applyTimer is not null)
        {
            _applyTimer.Stop();
            _applyTimer.Tick -= ApplyPendingZoom;
            _applyTimer = null;
        }
        _context = null;
    }
}
