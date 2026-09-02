using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>Drawer state owner and generic contribution renderer.</summary>
public sealed partial class ReaderDrawer : UserControl,
    IReaderFeature,
    IReaderVisualFeature,
    IReaderDrawerContributionHost
{
    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly ReaderActivityHub _activity;
    private readonly Border _backdrop;
    private readonly ObservableCollection<ReaderDrawerContribution> _contributions = [];
    private readonly ReadOnlyObservableCollection<ReaderDrawerContribution> _readOnlyContributions;
    private bool _disposed;

    internal ReaderDrawer(
        ReaderSessionState state,
        ReaderCommandHub commands,
        ReaderActivityHub activity)
    {
        _state = state;
        _commands = commands;
        _activity = activity;
        _readOnlyContributions = new ReadOnlyObservableCollection<ReaderDrawerContribution>(_contributions);
        InitializeComponent();

        _backdrop = new Border
        {
            Background = Brushes.Black,
            Opacity = 0,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Visuals =
        [
            new ReaderVisualContribution(ReaderLayer.DrawerBackdrop, _backdrop),
            new ReaderVisualContribution(ReaderLayer.Drawer, this),
        ];
    }

    public string FeatureName => "Drawer";
    public IReadOnlyList<ReaderVisualContribution> Visuals { get; }
    public ReadOnlyObservableCollection<ReaderDrawerContribution> Contributions => _readOnlyContributions;

    public void Attach(ReaderFeatureContext context)
    {
        _commands.ToggleDrawerRequested += ToggleDrawer;
        _commands.CloseDrawerRequested += CloseDrawer;
        _state.PropertyChanged += OnStateChanged;
        _activity.ActivityOccurred += OnActivity;
        ApplyState();
    }

    public void SetContributions(IReadOnlyList<ReaderDrawerContribution> contributions)
    {
        _contributions.Clear();
        foreach (var contribution in contributions) _contributions.Add(contribution);
    }

    private void ToggleDrawer() => SetOpen(!_state.IsDrawerOpen);

    private void CloseDrawer() => SetOpen(false);

    private void SetOpen(bool open)
    {
        if (_state.IsDrawerOpen == open) return;
        _state.SetDrawerOpen(open);
        if (open) _activity.Report(ReaderActivityOrigin.DrawerOpened);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IReaderStateView.IsDrawerOpen)) ApplyState();
    }

    private void OnActivity(object? sender, ReaderActivityEventArgs e)
    {
        if (_state.IsDrawerOpen
            && ReaderDrawerPolicy.ShouldCloseForActivity(e.Origin, _state.IsDrawerPinned))
        {
            SetOpen(false);
        }
    }

    private void ApplyState()
    {
        DrawerPanel.IsOpen = _state.IsDrawerOpen;
        _backdrop.Visibility = _state.IsDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
        _backdrop.Opacity = _state.IsDrawerOpen ? 0.22 : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.ToggleDrawerRequested -= ToggleDrawer;
        _commands.CloseDrawerRequested -= CloseDrawer;
        _state.PropertyChanged -= OnStateChanged;
        _activity.ActivityOccurred -= OnActivity;
        _contributions.Clear();
        DrawerPanel.IsOpen = false;
    }
}
