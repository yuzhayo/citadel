using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Citadel.Setting.Components;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>Owns manual wheel/touchpad scroll speed and its Drawer control.</summary>
public sealed class ReaderManualScrollController : IReaderFeature, IReaderDrawerContributionProvider
{
    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly SettingSlider _speed;
    private readonly TextBlock _speedValue;
    private readonly ReaderDrawerCardContribution _contribution;
    private ReaderFeatureContext? _context;
    private double _velocity;
    private double _targetVelocity;
    private long _glideLastFrame;
    private bool _gliding;
    private bool _rendering;
    private bool _syncingSlider;
    private bool _disposed;

    internal ReaderManualScrollController(ReaderSessionState state, ReaderCommandHub commands)
    {
        _state = state;
        _commands = commands;

        _speedValue = ReaderDrawerCards.Label(FormatSpeed(state.ManualScrollPercentPerTick));
        _speedValue.HorizontalAlignment = HorizontalAlignment.Right;
        _speed = new SettingSlider
        {
            Minimum = ReaderValuePolicy.MinimumManualScrollPercentPerTick,
            Maximum = ReaderValuePolicy.MaximumManualScrollPercentPerTick,
            Step = 1,
            SmallChange = 1,
            LargeChange = 1,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = state.ManualScrollPercentPerTick,
        };
        AutomationProperties.SetName(_speed, "Manual scroll speed");
        AutomationProperties.SetAutomationId(_speed, "ReaderManualScrollSpeed");

        var header = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = ReaderDrawerCards.Label("Speed");
        label.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(_speedValue, 1);
        header.Children.Add(label);
        header.Children.Add(_speedValue);

        var content = new StackPanel();
        content.Children.Add(ReaderDrawerCards.Label("Manual scroll"));
        content.Children.Add(header);
        content.Children.Add(_speed);
        _contribution = new ReaderDrawerCardContribution(
            "manual-scroll",
            320,
            ReaderDrawerCards.Create(content, "ReaderManualScrollCard"));

        _speed.ValueChanged += OnSpeedValueChanged;
    }

    public string FeatureName => "ManualScroll";
    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions => [_contribution];

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        context.Input.MouseWheel += OnMouseWheel;
        _commands.SetManualScrollSpeedRequested += SetSpeed;
        _state.PropertyChanged += OnStateChanged;
        SyncControls();
    }

    private void OnMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        var context = _context;
        if (context is null || e.Handled || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || _state.IsLoading || _state.HasError || _state.IsTransitioning)
        {
            return;
        }

        var point = context.Viewport.GetPointerPosition(e);
        if (point.X < 0 || point.Y < 0
            || point.X > context.Viewport.ViewportWidth || point.Y > context.Viewport.ViewportHeight)
        {
            return;
        }

        var distance = ReaderManualScrollPolicy.DistanceForWheel(
            e.Delta,
            context.Viewport.ViewportHeight,
            _state.ManualScrollPercentPerTick);
        if (Math.Abs(distance) < 0.001) return;

        // Wheel-down (negative delta) scrolls content down, so the offset grows.
        var offsetDelta = -distance;
        var viewport = context.Viewport;

        if (!SystemParameters.ClientAreaAnimation)
        {
            // Honor the OS "reduce motion" setting with a direct step.
            viewport.ScrollToVerticalOffset(
                ClampOffset(viewport.VerticalOffset + offsetDelta, viewport.ScrollableHeight),
                ReaderActivityOrigin.ManualWheel);
            e.Handled = true;
            return;
        }

        // Inject momentum into the target velocity; the actual velocity eases toward
        // it each frame (see OnRendering), so a lone notch ramps in instead of kicking.
        // Rapid notches accumulate, so a fast flick still flows and coasts.
        _targetVelocity = ReaderMomentumScrollPolicy.ClampVelocity(
            _targetVelocity + ReaderMomentumScrollPolicy.ImpulseForDistance(offsetDelta));
        StartGlide();
        e.Handled = true;
    }

    private void StartGlide()
    {
        if (!_gliding)
        {
            _gliding = true;
            _glideLastFrame = Stopwatch.GetTimestamp();
        }

        // Drive the glide off the compositor's per-frame callback (vsync-aligned),
        // the same clock auto-scroll uses. A DispatcherTimer is not synced to the
        // render loop and clumps under load, which reads as a tick between frames.
        SubscribeRendering();
    }

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
        var context = _context;
        if (_disposed || context is null || !_gliding)
        {
            UnsubscribeRendering();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = Stopwatch.GetElapsedTime(_glideLastFrame, now).TotalSeconds;
        _glideLastFrame = now;

        // Friction decays the momentum target; the actual velocity then eases toward
        // it so acceleration is continuous (no per-notch kick). Integrating off the
        // live offset keeps this correct when the coordinator shifts the offset across
        // a chapter seam, so no separate re-anchoring is needed.
        _targetVelocity = ReaderMomentumScrollPolicy.DecayVelocity(_targetVelocity, elapsedSeconds);
        _velocity = ReaderMomentumScrollPolicy.ApproachVelocity(_velocity, _targetVelocity, elapsedSeconds);

        var viewport = context.Viewport;
        var desired = viewport.VerticalOffset
            + ReaderMomentumScrollPolicy.DistanceForFrame(_velocity, elapsedSeconds);
        var clamped = ClampOffset(desired, viewport.ScrollableHeight);
        viewport.ScrollToVerticalOffset(clamped, ReaderActivityOrigin.ManualWheel);

        // Absorb velocity at a hard boundary so we do not keep pushing into it.
        if (Math.Abs(clamped - desired) > 0.001)
        {
            _velocity = 0;
            _targetVelocity = 0;
        }

        if (ReaderMomentumScrollPolicy.ShouldStop(_targetVelocity)
            && ReaderMomentumScrollPolicy.ShouldStop(_velocity))
        {
            StopGlide();
        }
    }

    private void StopGlide()
    {
        _gliding = false;
        _velocity = 0;
        _targetVelocity = 0;
        UnsubscribeRendering();
    }

    private static double ClampOffset(double offset, double scrollableHeight) =>
        Math.Clamp(offset, 0, Math.Max(0, scrollableHeight));

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
        _commands.SetManualScrollSpeed(value);
    }

    private void SetSpeed(double value) => _state.SetManualScrollPercentPerTick(value);

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IReaderStateView.ManualScrollPercentPerTick))
            SyncControls();
    }

    private void SyncControls()
    {
        _syncingSlider = true;
        _speed.Value = _state.ManualScrollPercentPerTick;
        _syncingSlider = false;
        _speedValue.Text = FormatSpeed(_state.ManualScrollPercentPerTick);
    }

    private static string FormatSpeed(double value) => $"{value:0}% / tick";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopGlide();
        if (_context is not null) _context.Input.MouseWheel -= OnMouseWheel;
        _commands.SetManualScrollSpeedRequested -= SetSpeed;
        _state.PropertyChanged -= OnStateChanged;
        _speed.ValueChanged -= OnSpeedValueChanged;
        _context = null;
    }
}
