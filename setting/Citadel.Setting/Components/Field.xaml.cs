using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>A text input whose placeholder and width can be supplied by preset.</summary>
public sealed partial class SettingField : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(SettingField),
            new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(SettingField),
            new FrameworkPropertyMetadata(string.Empty));

    public SettingField() => InitializeComponent();

    public event Action<string>? TextChanged;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SettingField)sender).TextChanged?.Invoke((string)(args.NewValue ?? string.Empty));
}
