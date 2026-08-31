using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>
/// Shared table actions: one action is centered; primary and secondary actions
/// share the left and right cell edges when both are present.
/// </summary>
public sealed class SettingTableActions : Control
{
    public static readonly DependencyProperty PrimaryContentProperty =
        DependencyProperty.Register(nameof(PrimaryContent), typeof(object), typeof(SettingTableActions));

    public static readonly DependencyProperty PrimaryContentTemplateProperty =
        DependencyProperty.Register(nameof(PrimaryContentTemplate), typeof(DataTemplate), typeof(SettingTableActions));

    public static readonly DependencyProperty SecondaryContentProperty =
        DependencyProperty.Register(nameof(SecondaryContent), typeof(object), typeof(SettingTableActions));

    public static readonly DependencyProperty SecondaryContentTemplateProperty =
        DependencyProperty.Register(nameof(SecondaryContentTemplate), typeof(DataTemplate), typeof(SettingTableActions));

    public SettingTableActions()
    {
        var resources = SharedComponentResources.Load("TableActions.xaml");
        Resources.MergedDictionaries.Add(resources);
        Style = (Style)resources["SettingTableActionsStyle"];
    }

    public object? PrimaryContent
    {
        get => GetValue(PrimaryContentProperty);
        set => SetValue(PrimaryContentProperty, value);
    }

    public DataTemplate? PrimaryContentTemplate
    {
        get => (DataTemplate?)GetValue(PrimaryContentTemplateProperty);
        set => SetValue(PrimaryContentTemplateProperty, value);
    }

    public object? SecondaryContent
    {
        get => GetValue(SecondaryContentProperty);
        set => SetValue(SecondaryContentProperty, value);
    }

    public DataTemplate? SecondaryContentTemplate
    {
        get => (DataTemplate?)GetValue(SecondaryContentTemplateProperty);
        set => SetValue(SecondaryContentTemplateProperty, value);
    }
}
