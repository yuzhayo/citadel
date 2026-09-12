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
    private QueueGroupProjection _projection = new();
    private DownloadQueueFeature? _queue;
    private bool _disposed;

    public DownloadListScreen()
    {
        InitializeComponent();
        JobTable.ItemsSource = _projection.Visible;
        JobTable.Loaded += (_, _) =>
        {
            foreach (var column in JobTable.InteractiveColumns) column.SortDirection = null;
        };
    }

    /// <summary>The only way back to the Downloader tab; the MangaReader parent selects it.</summary>
    public event EventHandler? BackRequested;

    /// <summary>
    /// Attaches the queue owner with the narrow dependency only. Called by the
    /// MangaReader composition before this top-level tab is loaded.
    /// </summary>
    public void UseQueue(DownloadQueueFeature queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (_disposed || _queue is not null) return;

        _queue = queue;
        _projection = QueueGroupProjection.For(queue);
        _projection.SelectionChanged += UpdateSelection;
        JobTable.ItemsSource = _projection.Visible;
        queue.QueueSummaryChanged += Queue_QueueSummaryChanged;
        Render(queue.Snapshot(), queue.Summary());
    }

    private void Queue_QueueSummaryChanged(object? sender, EventArgs e)
    {
        if (_disposed || _queue is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Queue_QueueSummaryChanged(sender, e));
            return;
        }

        Render(_queue.Snapshot(), _queue.Summary());
    }

    private void Render(IReadOnlyList<DownloadJobRecord> jobs, QueueSummary summary)
    {
        _projection.Update(jobs);
        UpdateSelection();
        EmptyText.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = summary.Total == 0
            ? "No jobs"
            : $"{summary.Active} active · {summary.Paused} paused · {summary.Failed} failed · {summary.Total} total";
        ResumeButton.IsEnabled = jobs.Any(job =>
            job.State is DownloadJobState.Paused or DownloadJobState.Failed);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queue is null) return;
        Run(() => _queue.ClearCompleted());
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queue is null) return;
        Run(() => _queue.ResumeAll());
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not QueueDisplayRow row || _queue is null) return;
        Run(() =>
        {
            var stop = (sender as ContentControl)?.Content?.ToString() == "Stop";
            if (row.IsGroup)
            {
                if (stop) _queue.StopGroup(row.GroupKey); else _queue.ResumeGroup(row.GroupKey);
            }
            else
            {
                if (stop) _queue.Stop(row.Id);
                else if (row.Job.State == DownloadJobState.Failed) _queue.Retry(row.Id);
                else _queue.Start(row.Id);
            }
        });
    }

    private void StopAll_Click(object sender, RoutedEventArgs e) { if (_queue is not null) Run(_queue.StopAll); }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QueueDisplayRow row) _projection.Toggle(row);
    }

    private void UpdateSelection()
    {
        RemoveSelectedButton.Content = $"Remove selected ({_projection.SelectedIds.Count})";
        RemoveSelectedButton.IsEnabled = _projection.SelectedIds.Count > 0;
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_queue is null) return;
        var ids = _projection.SelectedIds;
        if (ids.Count == 0 || !SettingDialog.Confirm(Window.GetWindow(this), "Downloader",
                $"Remove {ids.Count} selected jobs and their staged pages? Published CBZ files remain intact.", "Remove")) return;
        await RemoveAsync(ids);
    }

    private async Task RemoveAsync(IReadOnlyList<string> ids)
    {
        try
        {
            await _queue!.RemoveManyAsync(ids);
            if (!_disposed) SetStatus(null);
        }
        catch (Exception ex) { if (!_disposed) SetStatus("Remove failed: " + ex.Message); }
    }

    /// <summary>
    /// A cross-group fallback always replaces the whole chapter and always needs
    /// an explicit user confirmation; candidates are offered one at a time.
    /// </summary>
    private void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } job || _queue is null) return;
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

            Run(() => _queue.ConfirmSourceFallback(job.JobId, candidate));
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

        try
        {
            // Explorer's plain directory argument is not reliable when a path
            // contains spaces: it may fall back to Documents. Selecting the
            // published CBZ resolves its parent folder and makes the chapter
            // the visible target in the same action.
            Process.Start(new ProcessStartInfo("explorer.exe",
                "/select,\"" + path.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            SetStatus("Tidak dapat membuka folder: " + exception.GetBaseException().Message);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } job || _queue is null) return;

        // Staging is deleted only after the queue state is committed, and the
        // user is asked first when there is something to lose.
        if (_queue.HasStagedData(job.JobId))
        {
            var confirmed = SettingDialog.Confirm(
                Window.GetWindow(this),
                "Downloader",
                $"Hapus job '{job.TitleDisplayName} — {job.ChapterDisplayName}'?\n\nData staging yang sudah terunduh akan dibuang.",
                "Remove");
            if (!confirmed) return;
        }

        await RemoveAsync([job.JobId]);
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
        ((sender as FrameworkElement)?.Tag as QueueDisplayRow)?.Job;

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
        if (_queue is not null) _queue.QueueSummaryChanged -= Queue_QueueSummaryChanged;
        _projection.SelectionChanged -= UpdateSelection;
    }
}
