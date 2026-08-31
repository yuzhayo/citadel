using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>A shared two-sided toolbar; screens supply actions, not layout.</summary>
public sealed class SettingToolbar : Control
{
    public static readonly DependencyProperty LeftContentProperty =
        DependencyProperty.Register(nameof(LeftContent), typeof(object), typeof(SettingToolbar));
    public static readonly DependencyProperty LeftContentTemplateProperty =
        DependencyProperty.Register(nameof(LeftContentTemplate), typeof(DataTemplate), typeof(SettingToolbar));
    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(SettingToolbar));
    public static readonly DependencyProperty RightContentTemplateProperty =
        DependencyProperty.Register(nameof(RightContentTemplate), typeof(DataTemplate), typeof(SettingToolbar));

    public SettingToolbar()
    {
        var resources = SharedComponentResources.Load("Toolbar.xaml");
        Resources.MergedDictionaries.Add(resources);
        Style = (Style)resources["SettingToolbarStyle"];
    }

    public object? LeftContent
    {
        get => GetValue(LeftContentProperty);
        set => SetValue(LeftContentProperty, value);
    }
    public DataTemplate? LeftContentTemplate
    {
        get => (DataTemplate?)GetValue(LeftContentTemplateProperty);
        set => SetValue(LeftContentTemplateProperty, value);
    }
    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }
    public DataTemplate? RightContentTemplate
    {
        get => (DataTemplate?)GetValue(RightContentTemplateProperty);
        set => SetValue(RightContentTemplateProperty, value);
    }
}
