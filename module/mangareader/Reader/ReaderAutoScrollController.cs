using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Citadel.Setting.Components;

namespace Module.Mangareader;

public sealed class ReaderAutoScrollController : IReaderFeature, IReaderDrawerContributionProvider
{
    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly SettingButton _start;
    private readonly SettingButton _stop;
    private readonly SettingSlider _speed;
    private readonly TextBlock _speedValue;
    private readonly ReaderDrawerCardContribution _contribution;
    private ReaderFeatureContext? _context;
    private long _lastFrame;
    private bool _rendering;
    private bool _syncingSlider;
    private bool _disposed;

    internal ReaderAutoScrollController(ReaderSessionState state, ReaderCommandHub commands)
    {
        _state = state;
        _commands = commands;

        _start = new SettingButton
        {
            Content = "Start",
            Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_start, "Start auto-scroll");
        AutomationProperties.SetAutomationId(_start, "ReaderAutoScrollStart");
        _stop = new SettingButton
        {
            Content = "Stop",
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_stop, "Stop auto-scroll");
        AutomationProperties.SetAutomationId(_stop, "ReaderAutoScrollStop");

        var actions = new Grid { Margin = new Thickness(0, 8, 0, 10) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_start, 0);
        Grid.SetColumn(_stop, 1);
        actions.Children.Add(_start);
        actions.Children.Add(_stop);

        _speedValue = ReaderDrawerCards.Label(FormatSpeed(state.AutoScrollSecondsPerViewport));
        _speedValue.HorizontalAlignment = HorizontalAlignment.Right;

        _speed = new SettingSlider
        {
            Minimum = ReaderValuePolicy.MinimumAutoScrollSeconds,
            Maximum = ReaderValuePolicy.MaximumAutoScrollSeconds,
            Step = 1,
            SmallChange = 1,
            LargeChange = 1,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            IsDirectionReversed = true,
            Value = state.AutoScrollSecondsPerViewport,
        };
        AutomationProperties.SetName(_speed, "Auto-scroll speed");
        AutomationProperties.SetAutomationId(_speed, "ReaderAutoScrollSpeed");

        var speedHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        speedHeader.ColumnDefinitions.Add(new ColumnDefinition());
        speedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var speedLabel = ReaderDrawerCards.Label("Speed");
        speedLabel.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(speedLabel, 0);
        Grid.SetColumn(_speedValue, 1);
        speedHeader.Children.Add(speedLabel);
        speedHeader.Children.Add(_speedValue);

        var content = new StackPanel();
        content.Children.Add(ReaderDrawerCards.Label("Auto-scroll"));
        content.Children.Add(actions);
        content.Children.Add(speedHeader);
        content.Children.Add(_speed);

        _contribution = new ReaderDrawerCardContribution(
            "auto-scroll",
            300,
            ReaderDrawerCards.Create(content, "ReaderAutoScrollCard"));

        _start.Click += OnStartClick;
        _stop.Click += OnStopClick;
        _speed.ValueChanged += OnSpeedValueChanged;
    }

    public string FeatureName => "AutoScroll";
    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions => [_contribution];

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        _commands.StartAutoScrollRequested += Start;
        _commands.SetAutoScrollSpeedRequested += SetSpeed;
        _commands.StopAutoScrollRequested += Stop;
        _state.PropertyChanged += OnStateChanged;
        context.Activity.ActivityOccurred += OnActivity;
        UpdateControls();
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => _commands.StartAutoScroll();

    private void OnStopClick(object sender, RoutedEventArgs e) => _commands.StopAutoScroll();

    private void OnSpeedValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider) return;
        var value = _speed.Snap(e.NewValue);
        if (Math.Abs(value - e.NewValue) > 0.001)
        {
            _syncingSlider = true;
            _speed.Value = value;
            _syncingSlider = false;
        }
        _commands.SetAutoScrollSpeed(value);
    }

    private void Start()
    {
        if (_context is null || _state.IsLoading || _state.HasError
            || _context.Chapters.IsAtAbsoluteEnd)
        {
            return;
        }

        _lastFrame = Stopwatch.GetTimestamp();
        _state.SetAutoScrollRunning(true);
        SubscribeRendering();
    }

    private void Stop()
    {
        UnsubscribeRendering();
        _state.SetAutoScrollRunning(false);
    }

    private void SetSpeed(double seconds) =>
        _state.SetAutoScrollSecondsPerViewport(seconds);

    private void SubscribeRendering()
    {
        if (_rendering) return;
        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    private void UnsubscribeRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_context is null || !_state.IsAutoScrollRunning)
        {
            UnsubscribeRendering();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastFrame, now);
        _lastFrame = now;
        var delta = ReaderAutoScrollPolicy.DistanceForElapsed(
            _context.Viewport.ViewportHeight,
            _state.AutoScrollSecondsPerViewport,
            elapsed);
        _context.Viewport.ScrollToVerticalOffset(
            _context.Viewport.VerticalOffset + delta,
            ReaderActivityOrigin.AutoScroll);

        if (_context.Chapters.IsAtAbsoluteEnd) Stop();
    }

    private void OnActivity(object? sender, ReaderActivityEventArgs e)
    {
        if (_state.IsAutoScrollRunning && ReaderAutoScrollPolicy.StopsFor(e.Origin)) Stop();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IReaderStateView.AutoScrollSecondsPerViewport))
            SyncSlider();

        if (e.PropertyName is nameof(IReaderStateView.IsAutoScrollRunning)
            or nameof(IReaderStateView.AutoScrollSecondsPerViewport)
            or nameof(IReaderStateView.IsLoading)
            or nameof(IReaderStateView.HasError))
        {
            UpdateControls();
        }

        if (e.PropertyName is nameof(IReaderStateView.IsDrawerOpen)
            or nameof(IReaderStateView.IsLoading)
            or nameof(IReaderStateView.HasError))
        {
            if (_state.IsDrawerOpen || _state.IsLoading || _state.HasError) Stop();
        }
    }

    private void SyncSlider()
    {
        _syncingSlider = true;
        _speed.Value = _state.AutoScrollSecondsPerViewport;
        _syncingSlider = false;
    }

    private void UpdateControls()
    {
        _start.IsEnabled = !_state.IsAutoScrollRunning && !_state.IsLoading && !_state.HasError;
        _stop.IsEnabled = _state.IsAutoScrollRunning;
        _speedValue.Text = FormatSpeed(_state.AutoScrollSecondsPerViewport);
    }

    private static string FormatSpeed(double value) => $"{value:0} s / screen";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _start.Click -= OnStartClick;
        _stop.Click -= OnStopClick;
        _speed.ValueChanged -= OnSpeedValueChanged;
        _commands.StartAutoScrollRequested -= Start;
        _commands.SetAutoScrollSpeedRequested -= SetSpeed;
        _commands.StopAutoScrollRequested -= Stop;
        _state.PropertyChanged -= OnStateChanged;
        if (_context is not null) _context.Activity.ActivityOccurred -= OnActivity;
        _context = null;
    }
}
