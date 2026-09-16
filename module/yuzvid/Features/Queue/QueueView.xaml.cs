using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Module.Yuzvid.Features.Queue;

/// <summary>
/// Feature-owned queue surface. Binds <see cref="QueueEngine.Items"/>; buttons
/// translate clicks into engine commands. Engine is injected (module-built).
/// </summary>
public partial class QueueView : UserControl
{
    private readonly QueueEngine _engine;

    public QueueView(QueueEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        InitializeComponent();
        QueueTable.ItemsSource = _engine.Items;
        _engine.Items.CollectionChanged += (_, _) => RefreshSummary();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var active = _engine.Items.Count(i => i.Status == "Mengunduh" || i.Status == "Antre");
        var failed = _engine.Items.Count(i => i.Status == "Gagal");
        SummaryText.Text = $"{active} active · {failed} failed · {_engine.Items.Count} total";
    }

    private void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        _engine.ClearCompleted();
        RefreshSummary();
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _engine.Items.Where(i => i.Status == "Antre" || i.Status == "Gagal").ToList())
        {
            await _engine.StartAsync(item);
            RefreshSummary();
        }
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _engine.CancelAll();
        RefreshSummary();
    }

    private void RemoveFailed_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _engine.Items.Where(i => i.Status == "Gagal").ToList())
            _engine.Remove(item);
        RefreshSummary();
    }

    private async void RowStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is QueueItem item)
        {
            await _engine.StartAsync(item);
            RefreshSummary();
        }
    }

    private void RowStop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is QueueItem item)
        {
            _engine.Cancel(item);
            RefreshSummary();
        }
    }

    private void RowRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is QueueItem item)
        {
            _engine.Remove(item);
            RefreshSummary();
        }
    }
}
