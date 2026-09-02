using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Citadel.Setting.Components;

/// <summary>
/// Screen-blind left Drawer. The control fills its host while the interactive
/// panel uses <see cref="WidthFraction"/> of that host, so closed content never
/// leaves an invisible full-width input shield.
/// </summary>
public sealed partial class SettingDrawer : ContentControl
{
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(200);
    private static readonly CubicEase EaseOut = CreateEase();

    private Border? _surface;
    private TranslateTransform? _transform;
    private int _animationGeneration;
    private bool _loaded;

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(
            nameof(IsOpen),
            typeof(bool),
            typeof(SettingDrawer),
            new FrameworkPropertyMetadata(false, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty WidthFractionProperty =
        DependencyProperty.Register(
            nameof(WidthFraction),
            typeof(double),
            typeof(SettingDrawer),
            new FrameworkPropertyMetadata(0.25, OnVisualStatePropertyChanged),
            value => value is double fraction
                && double.IsFinite(fraction)
                && fraction is >= 0.1 and <= 0.9);

    private static readonly DependencyPropertyKey PanelWidthPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(PanelWidth),
            typeof(double),
            typeof(SettingDrawer),
            new FrameworkPropertyMetadata(0d));

    public static readonly DependencyProperty PanelWidthProperty = PanelWidthPropertyKey.DependencyProperty;

    public SettingDrawer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double WidthFraction
    {
        get => (double)GetValue(WidthFractionProperty);
        set => SetValue(WidthFractionProperty, value);
    }

    public double PanelWidth => (double)GetValue(PanelWidthProperty);

    public override void OnApplyTemplate()
    {
        CancelAnimation();
        base.OnApplyTemplate();
        _surface = GetTemplateChild("PART_DrawerSurface") as Border;
        _transform = _surface?.RenderTransform as TranslateTransform;
        UpdatePanelWidth();
        SnapToState();
    }

    private static CubicEase CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static void OnVisualStatePropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not SettingDrawer drawer) return;
        drawer.UpdatePanelWidth();
        drawer.AnimateToState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        UpdatePanelWidth();
        SnapToState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        CancelAnimation();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePanelWidth();
        SnapToState();
    }

    private void UpdatePanelWidth()
    {
        var width = Math.Max(0, ActualWidth * WidthFraction);
        SetValue(PanelWidthPropertyKey, width);
        if (_surface is not null) _surface.Width = width;
    }

    private void AnimateToState()
    {
        if (_transform is null) return;
        var target = IsOpen ? 0 : -PanelWidth;
        if (!_loaded || !SystemParameters.ClientAreaAnimation || PanelWidth <= 0)
        {
            Snap(target);
            return;
        }

        var generation = ++_animationGeneration;
        var current = _transform.X;
        _transform.BeginAnimation(TranslateTransform.XProperty, null);
        _transform.X = current;

        var animation = new DoubleAnimation(current, target, SlideDuration)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            if (generation == _animationGeneration) Snap(target);
        };
        _transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void SnapToState() => Snap(IsOpen ? 0 : -PanelWidth);

    private void Snap(double position)
    {
        if (_transform is null) return;
        _animationGeneration++;
        _transform.BeginAnimation(TranslateTransform.XProperty, null);
        _transform.X = position;
    }

    private void CancelAnimation()
    {
        _animationGeneration++;
        _transform?.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
