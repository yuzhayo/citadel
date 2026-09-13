using System.Windows;
using System.Windows.Controls;

namespace Module.Proxy.Features.Webshare;

public partial class WebshareView : UserControl, IDisposable
{
    private readonly WebshareCoordinator _coordinator;
    private bool _disposed;

    internal WebshareView(WebshareCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        InitializeComponent();
        SavedKeysTable.SetColumns(["#", "API key"]);
        _coordinator.StateChanged += Coordinator_StateChanged;
        RenderKeySummary();
        Coordinator_StateChanged(_coordinator, _coordinator.CurrentState);
    }

    private void AddKeysButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = _coordinator.AddKeys(ApiKeyField.Password);
            ApiKeyField.Clear();
            RenderKeySummary();
            ProcessButton.IsEnabled = !_coordinator.CurrentState.IsRunning && count > 0;
            StatusText.Text = $"Saved {count} Webshare API key(s).";
        }
        catch (Exception error)
        {
            StatusText.Text = "Keys were not saved: " + error.Message;
        }
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        await _coordinator.ProcessAsync();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _coordinator.Stop();

    private void ClearKeysButton_Click(object sender, RoutedEventArgs e)
    {
        _coordinator.ClearKeys();
        ApiKeyField.Clear();
        RenderKeySummary();
        StatusText.Text = "Saved Webshare API keys were removed.";
    }

    private void Coordinator_StateChanged(object? sender, WebshareImportProgress state)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Coordinator_StateChanged(sender, state));
            return;
        }

        ProcessButton.IsEnabled = !state.IsRunning && _coordinator.KeyCount > 0;
        StopButton.IsEnabled = state.IsRunning;
        StatusText.Text = state.Status;
        ProgressBar.IsIndeterminate = state.IsRunning && state.Candidates == 0;
        ProgressBar.Maximum = Math.Max(1, state.Candidates);
        ProgressBar.Value = Math.Min(state.Checked, ProgressBar.Maximum);
        DetailText.Text = state.Candidates == 0
            ? $"API keys {state.KeysCompleted}/{state.KeyCount}"
            : $"Keys {state.KeysCompleted}/{state.KeyCount} · Candidates {state.Candidates} · " +
              $"Checked {state.Checked} · Reachable {state.Reachable} · Failed {state.Failed}";
        RenderKeySummary();
    }

    private void RenderKeySummary()
    {
        var keys = _coordinator.SavedKeys;
        KeySummaryText.Text = $"{keys.Count} API key(s) saved in the local Credenz vault.";
        SavedKeysTable.SetRows(keys.Select((key, index) =>
            (IReadOnlyList<string>)[$"{index + 1}", key]));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.StateChanged -= Coordinator_StateChanged;
    }
}
