using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>Shared modal chrome and confirmation behaviour for setting screens.</summary>
public class SettingDialog : Window
{
    public SettingDialog()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var resources = SharedComponentResources.Load("Dialog.xaml");
        Resources.MergedDictionaries.Add(resources);
        Style = (Style)resources["SettingDialogStyle"];
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string acceptLabel = "Delete")
    {
        var dialog = new SettingDialog
        {
            Owner = owner,
            Title = title,
            Width = 460,
        };
        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };
        messageBlock.SetResourceReference(TextBlock.ForegroundProperty, "Body");

        var accept = new SettingButton { Content = acceptLabel, MinWidth = 88 };
        var cancel = new SettingButton
        {
            Content = "Cancel",
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
        };
        accept.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actions.Children.Add(accept);
        actions.Children.Add(cancel);

        var content = new StackPanel();
        content.Children.Add(messageBlock);
        content.Children.Add(actions);
        dialog.Content = content;
        return dialog.ShowDialog() == true;
    }
}
