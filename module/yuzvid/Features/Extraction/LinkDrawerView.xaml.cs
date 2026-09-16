using System;
using System.Windows;
using System.Windows.Controls;

namespace Module.Yuzvid.Features.Extraction;

/// <summary>
/// Feature-owned link list. Shows ExtractionResults; raises commands the
/// parent routes (Copy now, Queue in T4). No extraction logic lives here.
/// </summary>
public partial class LinkDrawerView : UserControl
{
    public LinkDrawerView()
    {
        InitializeComponent();
    }

    public event EventHandler<VideoLink>? CopyRequested;
    public event EventHandler<VideoLink>? QueueRequested;
    public event EventHandler? RescanRequested;

    public void ShowResults(ExtractionResult result)
    {
        LinkList.ItemsSource = null;
        LinkList.ItemsSource = result.Links;
        EmptyText.Visibility = result.Links.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = result.Links.Count == 0 ? "no links" : string.Empty;
        HeaderText.Text = $"{result.Links.Count} link{(result.Links.Count != 1 ? "s" : "")}";
        StatusText.Text = result.StaticSkipped ? result.StaticSkipReason ?? string.Empty : string.Empty;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VideoLink link)
            CopyRequested?.Invoke(this, link);
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VideoLink link)
            QueueRequested?.Invoke(this, link);
    }

    private void RescanButton_Click(object sender, RoutedEventArgs e)
        => RescanRequested?.Invoke(this, EventArgs.Empty);
}
