using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

public enum SettingViewportMode
{
    Contained,
    Document,
}

/// <summary>
/// Shared finite screen root. Contained screens delegate scrolling to a table,
/// collection, or overlay; document screens own one vertical fallback scroller.
/// </summary>
public sealed class SettingViewport : ContentControl
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(SettingViewportMode),
            typeof(SettingViewport),
            new FrameworkPropertyMetadata(
                SettingViewportMode.Contained,
                FrameworkPropertyMetadataOptions.AffectsMeasure
                    | FrameworkPropertyMetadataOptions.AffectsArrange));

    public SettingViewport()
    {
        var resources = SharedComponentResources.Load("Viewport.xaml");
        Resources.MergedDictionaries.Add(resources);
        Style = (Style)resources["SettingViewportStyle"];
    }

    public SettingViewportMode Mode
    {
        get => (SettingViewportMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }
}
