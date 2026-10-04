using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

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
        RefreshDownloadFolderText();
    }

    private void RefreshDownloadFolderText()
        => DownloadFolderText.Text = _engine.DownloadFolder;

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var current = _engine.DownloadFolder;
        var dialog = new OpenFolderDialog
        {
            Title = "Pilih folder simpan unduhan",
            Multiselect = false,
            InitialDirectory = Directory.Exists(current) ? current : string.Empty,
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true) return;

        try
        {
            _engine.SetDownloadFolder(dialog.FolderName);
            RefreshDownloadFolderText();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Tidak dapat mengganti folder: " + ex.Message,
                "Yuzvid", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
        => OpenInExplorer(_engine.DownloadFolder, selectFile: false);

    private void RowOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not QueueItem item) return;

        // Finished file → reveal it; otherwise open the download folder.
        if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            OpenInExplorer(item.FilePath!, selectFile: true);
        else
            OpenInExplorer(_engine.DownloadFolder, selectFile: false);
    }

    private static void OpenInExplorer(string path, bool selectFile)
    {
        try
        {
            var target = selectFile
                ? "/select,\"" + path.Replace("\"", string.Empty, StringComparison.Ordinal) + "\""
                : "\"" + path.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Tidak dapat membuka folder: " + ex.Message,
                "Yuzvid", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
