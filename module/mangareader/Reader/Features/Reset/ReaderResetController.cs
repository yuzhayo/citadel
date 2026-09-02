using System.Windows;
using System.Windows.Automation;
using Citadel.Setting.Components;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public sealed class ReaderResetController : IReaderFeature, IReaderDrawerContributionProvider
{
    private readonly IReaderStateView _state;
    private readonly ReaderCommandHub _commands;
    private readonly SettingButton _action;
    private readonly ReaderDrawerCardContribution _contribution;
    private bool _disposed;

    internal ReaderResetController(IReaderStateView state, ReaderCommandHub commands)
    {
        _state = state;
        _commands = commands;
        _action = new SettingButton
        {
            Content = "Reset",
            ToolTip = "Reset zoom, dim, auto-scroll speed/running state, and Pin",
        };
        AutomationProperties.SetName(_action, "Reset controls");
        AutomationProperties.SetAutomationId(_action, "ReaderResetAction");
        _action.Click += OnActionClick;
        _contribution = new ReaderDrawerCardContribution(
            "global-reset",
            700,
            new SettingActionCard
            {
                Content = ReaderDrawerCards.Label("Reader controls"),
                ActionContent = _action,
            });
    }

    public string FeatureName => "Reset";
    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions => [_contribution];

    public void Attach(ReaderFeatureContext context) =>
        _commands.ResetAllRequested += Reset;

    private void OnActionClick(object sender, RoutedEventArgs e) => _commands.ResetAll();

    private void Reset()
    {
        _commands.StopAutoScroll();
        _commands.SetAutoScrollSpeed(ReaderValuePolicy.DefaultAutoScrollSeconds);
        _commands.ResetZoom(ReaderActivityOrigin.ControlsReset);
        _commands.ResetDim();
        if (_state.IsDrawerPinned) _commands.TogglePin();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.ResetAllRequested -= Reset;
        _action.Click -= OnActionClick;
    }
}
