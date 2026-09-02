using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Citadel.Setting.Components;

namespace Module.Mangareader;

public sealed class ReaderDimController : IReaderFeature,
    IReaderVisualFeature,
    IReaderDrawerContributionProvider
{
    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly Border _overlay;
    private readonly SettingSlider _slider;
    private readonly TextBlock _valueText;
    private readonly SettingButton _reset;
    private readonly ReaderDrawerCardContribution _contribution;
    private ReaderFeatureContext? _context;
    private bool _syncingSlider;
    private bool _disposed;

    internal ReaderDimController(ReaderSessionState state, ReaderCommandHub commands)
    {
        _state = state;
        _commands = commands;
        _overlay = new Border
        {
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _valueText = ReaderDrawerCards.Label(Format(state.DimPercent));
        _valueText.HorizontalAlignment = HorizontalAlignment.Right;
        _slider = new SettingSlider
        {
            Minimum = ReaderValuePolicy.MinimumDimPercent,
            Maximum = ReaderValuePolicy.MaximumDimPercent,
            Step = ReaderValuePolicy.DimStep,
            SmallChange = ReaderValuePolicy.DimStep,
            LargeChange = ReaderValuePolicy.DimStep,
            TickFrequency = ReaderValuePolicy.DimStep,
            IsSnapToTickEnabled = true,
            Value = state.DimPercent,
        };
        AutomationProperties.SetName(_slider, "Dim Pages");
        AutomationProperties.SetAutomationId(_slider, "ReaderDimSlider");
        _reset = new SettingButton
        {
            Content = "Reset Dim",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(_reset, "ReaderDimReset");

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = ReaderDrawerCards.Label("Dim Pages");
        label.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(_valueText, 1);
        header.Children.Add(label);
        header.Children.Add(_valueText);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(_slider);
        content.Children.Add(_reset);
        _contribution = new ReaderDrawerCardContribution(
            "dim",
            600,
            ReaderDrawerCards.Create(content, "ReaderDimCard"));
        _slider.ValueChanged += OnSliderValueChanged;
        _reset.Click += OnResetClick;
        Visuals = [new ReaderVisualContribution(ReaderLayer.Dim, _overlay)];
        DrawerContributions = [_contribution];
    }

    public string FeatureName => "Dim";
    public IReadOnlyList<ReaderVisualContribution> Visuals { get; }
    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions { get; }

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        _commands.SetDimRequested += SetDim;
        _commands.ChangeDimRequested += ChangeDim;
        _commands.ResetDimRequested += ResetDim;
        _state.PropertyChanged += OnStateChanged;
        context.Viewport.SizeChanged += OnViewportSizeChanged;
        Apply();
    }

    private void SetDim(double percent) => _state.SetDimPercent(percent);

    private void ChangeDim(int steps) =>
        _state.SetDimPercent(_state.DimPercent + (steps * ReaderValuePolicy.DimStep));

    private void ResetDim() => _state.SetDimPercent(ReaderValuePolicy.MinimumDimPercent);

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider) return;
        _commands.SetDim(_slider.Snap(e.NewValue));
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => _commands.ResetDim();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IReaderStateView.DimPercent)) return;
        _syncingSlider = true;
        _slider.Value = _state.DimPercent;
        _syncingSlider = false;
        _valueText.Text = Format(_state.DimPercent);
        Apply();
    }

    private void OnViewportSizeChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        if (_context is null) return;
        _overlay.Width = Math.Max(0, _context.Viewport.ViewportWidth);
        _overlay.Height = Math.Max(0, _context.Viewport.ViewportHeight);
        _overlay.Opacity = _state.DimPercent / 100;
        _overlay.Visibility = _state.DimPercent <= 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string Format(double value) => $"{value:0}%";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.SetDimRequested -= SetDim;
        _commands.ChangeDimRequested -= ChangeDim;
        _commands.ResetDimRequested -= ResetDim;
        _state.PropertyChanged -= OnStateChanged;
        _slider.ValueChanged -= OnSliderValueChanged;
        _reset.Click -= OnResetClick;
        if (_context is not null) _context.Viewport.SizeChanged -= OnViewportSizeChanged;
        _context = null;
    }
}
