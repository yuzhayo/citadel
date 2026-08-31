using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>Shared themed password input; password ownership stays with its screen.</summary>
public sealed partial class SettingPasswordField : UserControl
{
    public SettingPasswordField() => InitializeComponent();

    public event RoutedEventHandler? PasswordChanged;

    public string Password => InnerPassword.Password;

    public void Clear() => InnerPassword.Clear();

    public new bool Focus() => InnerPassword.Focus();

    private void InnerPassword_PasswordChanged(object sender, RoutedEventArgs e) =>
        PasswordChanged?.Invoke(this, e);
}
