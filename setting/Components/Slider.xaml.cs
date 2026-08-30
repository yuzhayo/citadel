using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>A numeric slider; step snapping is control behavior, not screen policy.</summary>
public sealed partial class SettingSlider : Slider
{
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(
            nameof(Step),
            typeof(double),
            typeof(SettingSlider),
            new FrameworkPropertyMetadata(1d),
            value => value is double step && double.IsFinite(step) && step > 0);

    public SettingSlider() => InitializeComponent();

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double Snap(double value)
    {
        var clamped = Math.Clamp(value, Minimum, Maximum);
        var steps = Math.Round((clamped - Minimum) / Step, MidpointRounding.AwayFromZero);
        return Math.Clamp(Minimum + (steps * Step), Minimum, Maximum);
    }
}
