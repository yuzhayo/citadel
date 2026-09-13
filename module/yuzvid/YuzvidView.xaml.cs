using System.Windows;
using System.Windows.Controls;

namespace Module.Yuzvid;

/// <summary>Yuzvid presentation shell. Backend wiring is intentionally absent.</summary>
public partial class YuzvidView : UserControl
{
    public YuzvidView()
    {
        InitializeComponent();
        PlaceholderTable.ItemsSource = new[]
        {
            new PlaceholderTableRow("Placeholder item", "Not wired", "Waiting for backend"),
        };
    }

    private void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string title }) return;

        DetailTitle.Text = title;
        MainWorkspace.Visibility = Visibility.Collapsed;
        DetailScreen.Visibility = Visibility.Visible;
    }

    private void BackToLibrary_Click(object sender, RoutedEventArgs e)
    {
        DetailScreen.Visibility = Visibility.Collapsed;
        MainWorkspace.Visibility = Visibility.Visible;
    }

    private sealed record PlaceholderTableRow(string Item, string Type, string Status);
}
