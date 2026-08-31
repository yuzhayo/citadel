using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>A compact shared card with flexible primary content and right-aligned actions.</summary>
public sealed class SettingActionCard : ContentControl
{
    public static readonly DependencyProperty ActionContentProperty =
        DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(SettingActionCard));

    public static readonly DependencyProperty ActionContentTemplateProperty =
        DependencyProperty.Register(nameof(ActionContentTemplate), typeof(DataTemplate), typeof(SettingActionCard));

    public SettingActionCard()
    {
        var resources = SharedComponentResources.Load("ActionCard.xaml");
        Resources.MergedDictionaries.Add(resources);
        Style = (Style)resources["SettingActionCardStyle"];
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public DataTemplate? ActionContentTemplate
    {
        get => (DataTemplate?)GetValue(ActionContentTemplateProperty);
        set => SetValue(ActionContentTemplateProperty, value);
    }
}
