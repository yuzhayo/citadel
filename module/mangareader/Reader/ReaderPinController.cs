using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Shapes;
using Citadel.Setting.Components;

namespace Module.Mangareader;

public sealed class ReaderPinController : IReaderFeature, IReaderDrawerContributionProvider
{
    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly SettingButton _action;
    private readonly ReaderDrawerCardContribution _contribution;
    private bool _disposed;

    internal ReaderPinController(ReaderSessionState state, ReaderCommandHub commands)
    {
        _state = state;
        _commands = commands;
        var icon = new Path
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M4,1 L12,1 L11,3 L11,7 L13,9 L13,10 L9,10 L9,15 L7,15 L7,10 L3,10 L3,9 L5,7 L5,3 Z"),
        };
        icon.SetResourceReference(Shape.FillProperty, "Fg");
        _action = new SettingButton
        {
            Width = 34,
            Height = 30,
            Padding = new Thickness(7),
            Content = icon,
            ToolTip = "Pin Drawer",
        };
        AutomationProperties.SetName(_action, "Pin Drawer");
        AutomationProperties.SetAutomationId(_action, "ReaderPinAction");
        _action.Click += OnActionClick;
        _contribution = new ReaderDrawerCardContribution(
            "drawer-pin",
            400,
            new SettingActionCard
            {
                Content = ReaderDrawerCards.Label("Keep Drawer open"),
                ActionContent = _action,
            });
    }

    public string FeatureName => "Pin";
    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions => [_contribution];

    public void Attach(ReaderFeatureContext context)
    {
        _commands.TogglePinRequested += TogglePin;
        _state.PropertyChanged += OnStateChanged;
        UpdateLabel();
    }

    private void TogglePin() => _state.SetDrawerPinned(!_state.IsDrawerPinned);

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IReaderStateView.IsDrawerPinned)) UpdateLabel();
    }

    private void OnActionClick(object sender, RoutedEventArgs e) => _commands.TogglePin();

    private void UpdateLabel()
    {
        var label = _state.IsDrawerPinned ? "Unpin Drawer" : "Pin Drawer";
        _action.ToolTip = label;
        AutomationProperties.SetName(_action, label);
        _action.Opacity = _state.IsDrawerPinned ? 1 : 0.72;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.TogglePinRequested -= TogglePin;
        _state.PropertyChanged -= OnStateChanged;
        _action.Click -= OnActionClick;
    }
}
