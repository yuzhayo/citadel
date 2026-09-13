using System.Windows.Controls;

namespace Module.Yuzvid.Features.Queue;

/// <summary>Queue presentation placeholder. It owns no transfer state or actions.</summary>
public partial class QueuePlaceholderView : UserControl
{
    public QueuePlaceholderView()
    {
        InitializeComponent();
        QueueTable.ItemsSource = new[]
        {
            new QueuePlaceholderRow(
                "Queue placeholder",
                "Not wired",
                "Idle",
                "—",
                "Connect the Yuzvid backend to populate queue work."),
        };
    }

    private sealed record QueuePlaceholderRow(
        string Title,
        string Item,
        string Status,
        string Progress,
        string Detail);
}
