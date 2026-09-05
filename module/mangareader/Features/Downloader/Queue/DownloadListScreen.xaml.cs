using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Citadel.Setting.Components;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>
/// Queue presentation only. It binds immutable job snapshots and forwards
/// actions to the queue owner; it holds no job state, never mutates a sibling's
/// collection, and route changes never dispose the queue.
/// </summary>
public partial class DownloadListScreen : UserControl, IDisposable
{
    private readonly ObservableCollection<DownloadJobRecord> _rows = [];
    private DownloaderContext? _context;
    private bool _disposed;

    public DownloadListScreen()
    {
        InitializeComponent();
        JobTable.ItemsSource = _rows;
    }

    /// <summary>The only way back to Catalog; this screen is not a tab.</summary>
    public event EventHandler? BackRequested;

    public void UseContext(DownloaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed || _context is not null) return;

        _context = context;
        context.Queue.QueueSummaryChanged += Queue_QueueSummaryChanged;
        Render(context.Queue.Snapshot(), context.Queue.Summary());
    }

    private void Queue_QueueSummaryChanged(object? sender, EventArgs e)
    {
        if (_disposed || _context is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Queue_QueueSummaryChanged(sender, e));
            return;
        }

        Render(_context.Queue.Snapshot(), _context.Queue.Summary());
    }

    private void Render(IReadOnlyList<DownloadJobRecord> jobs, QueueSummary summary)
    {
        _rows.Clear();
        foreach (var job in jobs)
        {
            _rows.Add(job);
        }

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = summary.Total == 0
            ? "No jobs"
            : $"{summary.Active} active · {summary.Paused} paused · {summary.Failed} failed · {summary.Total} total";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        Run(() => _context.Queue.ClearCompleted());
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } job || _context is null) return;

        // A paused or failed job resumes; anything else is asked to park. Pause
        // never terminates the browser or the pyhost process.
        Run(() =>
        {
            if (job.State is DownloadJobState.Paused or DownloadJobState.Failed)
            {
                _context.Queue.Resume(job.JobId);
            }
            else
            {
                _context.Queue.Pause(job.JobId);
            }
        });
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } job || _context is null) return;
        Run(() => _context.Queue.Resume(job.JobId));
    }

    /// <summary>
    /// A cross-group fallback always replaces the whole chapter and always needs
    /// an explicit user confirmation; candidates are offered one at a time.
    /// </summary>
    private void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } job || _context is null) return;
        if (!job.CanChooseFallback)
        {
            SetStatus("Job ini tidak sedang menunggu fallback source.");
            return;
        }

        foreach (var candidate in job.FallbackCandidates)
        {
            var accepted = SettingDialog.Confirm(
                Window.GetWindow(this),
                "Downloader",
                $"Ganti seluruh chapter ke group '{candidate.GroupName}'?\n\nAlasan: {candidate.Reason}\n\nChapter akan diunduh ulang dari awal dengan identitas group baru.",
                "Use this group");
            if (!accepted) continue;

            Run(() => _context.Queue.ConfirmSourceFallback(job.JobId, candidate));
            return;
        }

        SetStatus("Tidak ada group pengganti yang dipilih; job tetap menunggu.");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } job) return;
        if (job.PublishedPath is not { } path || !File.Exists(path))
        {
            SetStatus("File yang dipublikasikan tidak ditemukan.");
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetStatus("Tidak dapat membuka folder: " + exception.GetBaseException().Message);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } job || _context is null) return;

        // Staging is deleted only after the queue state is committed, and the
        // user is asked first when there is something to lose.
        if (_context.Queue.HasStagedData(job.JobId))
        {
            var confirmed = SettingDialog.Confirm(
                Window.GetWindow(this),
                "Downloader",
                $"Hapus job '{job.TitleDisplayName} — {job.ChapterDisplayName}'?\n\nData staging yang sudah terunduh akan dibuang.",
                "Remove");
            if (!confirmed) return;
        }

        Run(() => _context.Queue.Remove(job.JobId));
    }

    private void Run(Action action)
    {
        try
        {
            action();
            SetStatus(null);
        }
        catch (QueuePersistenceException exception)
        {
            // A transition that cannot be persisted must not be reported as
            // done; the queue is unchanged.
            SetStatus("Queue tidak dapat disimpan: " + exception.Message);
        }
    }

    private static DownloadJobRecord? Row(object sender) =>
        (sender as FrameworkElement)?.Tag as DownloadJobRecord;

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The queue itself outlives this screen: only the subscription goes.
        if (_context is not null) _context.Queue.QueueSummaryChanged -= Queue_QueueSummaryChanged;
        _rows.Clear();
    }
}
