using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    // Page workers can finish in parallel. The screen owns a bounded latest-
    // snapshot render cadence instead of posting one Dispatcher item per page.
    private readonly DispatcherTimer _renderTimer;
    private int _renderPending;
    private int _timerRunning;
    private int _timerStartQueued;
    private DateTimeOffset _lastActivityRenderUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public DownloadListScreen()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _renderTimer.Tick += RenderTimer_Tick;
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
        Interlocked.Exchange(ref _renderPending, 1);
        EnsureRenderTimerQueued();
    }

    private void EnsureRenderTimerQueued()
    {
        if (Volatile.Read(ref _timerRunning) != 0 || _disposed) return;
        if (Interlocked.Exchange(ref _timerStartQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _timerStartQueued, 0);
            if (_disposed || _queue is null) return;
            if (_renderTimer.IsEnabled) return;
            Volatile.Write(ref _timerRunning, 1);
            _renderTimer.Start();
        }));
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _queue is null)
        {
            StopRenderTimer();
            return;
        }

        var jobs = _queue.Snapshot();
        var now = DateTimeOffset.UtcNow;
        var active = jobs.Any(job => job.IsInFlight);
        var hasProgress = Interlocked.Exchange(ref _renderPending, 0) != 0;
        var refreshActivityAge = active && now - _lastActivityRenderUtc >= TimeSpan.FromSeconds(1);
        if (hasProgress || refreshActivityAge)
        {
            Render(jobs, _queue.Summary(), now);
            if (refreshActivityAge) _lastActivityRenderUtc = now;
        }

        if (!active && Volatile.Read(ref _renderPending) == 0) StopRenderTimer();
    }

    private void StopRenderTimer()
    {
        _renderTimer.Stop();
        Volatile.Write(ref _timerRunning, 0);
        if (Volatile.Read(ref _renderPending) != 0) EnsureRenderTimerQueued();
    }

    private void Render(IReadOnlyList<DownloadJobRecord> jobs, QueueSummary summary, DateTimeOffset? now = null)
    {
        _projection.Update(jobs, now);
        UpdateSelection();
        EmptyText.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = summary.Total == 0
            ? "No jobs"
            : $"Manifest {summary.ManifestActive}/{DownloadQueueFeature.ManifestConcurrency} · "
              + $"ready {summary.ManifestReady} · download {summary.DownloadActive}/{DownloadQueueFeature.JobConcurrency} · "
              + $"{summary.Paused} paused · {summary.Failed} failed · {summary.Total} total";
        ResumeButton.IsEnabled = jobs.Any(job => job.CanResume);
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
        _renderTimer.Stop();
        _renderTimer.Tick -= RenderTimer_Tick;
    }
}
