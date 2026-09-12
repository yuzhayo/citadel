using System.Windows;
using System.Windows.Controls;

namespace Module.Proxy.Features.Sync;

public partial class SyncView : UserControl, IDisposable
{
    private readonly ProxySyncCoordinator _coordinator;
    private bool _disposed;

    internal SyncView(ProxySyncCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        InitializeComponent();
        _coordinator.StateChanged += Coordinator_StateChanged;
        Coordinator_StateChanged(_coordinator, _coordinator.CurrentState);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        await _coordinator.StartAsync();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _coordinator.Stop();

    private void Coordinator_StateChanged(object? sender, ProxySyncState state)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Coordinator_StateChanged(sender, state));
            return;
        }

        StartButton.IsEnabled = !state.IsRunning;
        StopButton.IsEnabled = state.IsRunning;
        StatusText.Text = state.Status;
        if (state.Progress is { } progress)
        {
            ProgressBar.IsIndeterminate = progress.Phase == "Fetching sources";
            ProgressBar.Maximum = Math.Max(1, progress.Candidates);
            ProgressBar.Value = Math.Min(progress.Tested, ProgressBar.Maximum);
            DetailText.Text =
                $"Sources {progress.SourcesCompleted}/{progress.SourcesTotal} · " +
                $"Candidates {progress.Candidates} · Checked {progress.Tested} · " +
                $"Reachable {progress.Reachable} · Failed {progress.Failed} · " +
                $"{progress.Elapsed:mm\\:ss}";
        }
        else if (state.Result is { } result)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = Math.Max(1, result.Tested);
            ProgressBar.Value = result.Tested;
            var failedSources = result.Sources.Count(item => item.Error is not null);
            DetailText.Text =
                $"Published {result.Reachable.Count} · Checked {result.Tested} · " +
                $"Source failures {failedSources} · Skipped {result.Skipped} · " +
                $"Banned {result.Banned} · {result.Elapsed:mm\\:ss}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.StateChanged -= Coordinator_StateChanged;
    }
}
