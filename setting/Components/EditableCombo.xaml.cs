using System.Windows.Controls;
using System.Windows;

namespace Citadel.Setting.Components;

/// <summary>An editable text field with the shared dropdown presentation.</summary>
public sealed partial class SettingEditableCombo : ComboBox
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(SettingEditableCombo),
            new FrameworkPropertyMetadata(string.Empty));

    public SettingEditableCombo() => InitializeComponent();

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }
}
