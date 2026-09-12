using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CitadelBridge;
using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Pool;

internal sealed class ProxyPoolRow(ProxyEndpoint endpoint, ProxyHealthRecord? health) : INotifyPropertyChanged
{
    private bool _selected;
    public ProxyEndpoint Endpoint { get; } = endpoint;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
    public string Masked => Endpoint.Masked;
    public string Scheme => Endpoint.Scheme;
    public string Host => Endpoint.Host;
    public int Port => Endpoint.Port;
    public string Health => health?.State switch
    {
        ProxyHealthState.Healthy => "Healthy",
        ProxyHealthState.Unreachable => "Failed",
        _ => "Unknown",
    };
    public ProxyHealthState HealthState => health?.State ?? ProxyHealthState.Unknown;
    public int HealthSort => health?.State switch
    {
        ProxyHealthState.Healthy => 0,
        ProxyHealthState.Unknown => 1,
        _ => 2,
    };
    public string Latency => health?.LatencyMilliseconds is long value ? $"{value} ms" : "—";
    public long LatencySort => health?.LatencyMilliseconds ?? long.MaxValue;
    public string Checked => health?.CheckedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public long CheckedSort => health?.CheckedAtUtc.UtcTicks ?? 0;
    public string Diagnostic => health?.Detail ?? "Not checked";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class PoolView : UserControl, IDisposable
{
    private readonly ProxyPoolStore _store;
    private readonly PoolHealthCoordinator _healthCoordinator;
    private readonly ObservableCollection<ProxyPoolRow> _rows = [];
    private bool _disposed;

    internal PoolView(ProxyPoolStore store, PoolHealthCoordinator healthCoordinator)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _healthCoordinator = healthCoordinator ?? throw new ArgumentNullException(nameof(healthCoordinator));
        InitializeComponent();
        PoolTable.ItemsSource = _rows;
        _healthCoordinator.StateChanged += HealthCoordinator_StateChanged;
        _healthCoordinator.HealthSaved += HealthCoordinator_HealthSaved;
        Reload();
    }

    internal void Reload()
    {
        if (_disposed) return;
        try
        {
            var snapshot = _store.LoadActive();
            var health = _store.LoadHealth().Entries;
            _rows.Clear();
            foreach (var endpoint in snapshot.Endpoints)
            {
                health.TryGetValue(ProxyPoolHealthContract.EndpointKey(endpoint), out var record);
                _rows.Add(new ProxyPoolRow(endpoint, record));
            }
            var healthy = _rows.Count(row => row.Health == "Healthy");
            var failed = _rows.Count(row => row.HealthState == ProxyHealthState.Unreachable);
            SummaryText.Text = $"Committed pool: {_rows.Count} proxies · {healthy} healthy · {failed} failed";
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BanButton.IsEnabled = _rows.Count > 0;
            RemoveFailedButton.IsEnabled = failed > 0 && !_healthCoordinator.IsRunning;
            RefreshHealthButton.IsEnabled = _rows.Count > 0 && !_healthCoordinator.IsRunning;
            StopHealthButton.IsEnabled = _healthCoordinator.IsRunning;
            SetStatus(snapshot.SkippedLines > 0
                ? $"Skipped {snapshot.SkippedLines} malformed lines while reading the pool."
                : null);
        }
        catch (Exception ex)
        {
            SetStatus("Pool load failed: " + ex.Message);
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => Reload();

    private async void RefreshHealthButton_Click(object sender, RoutedEventArgs e) => await _healthCoordinator.RefreshAsync();

    private void StopHealthButton_Click(object sender, RoutedEventArgs e) => _healthCoordinator.Stop();

    private void HealthCoordinator_HealthSaved(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HealthCoordinator_HealthSaved(sender, e));
            return;
        }
        Reload();
    }

    private void HealthCoordinator_StateChanged(object? sender, PoolHealthProgress state)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HealthCoordinator_StateChanged(sender, state));
            return;
        }
        RefreshHealthButton.IsEnabled = !state.IsRunning && _rows.Count > 0;
        StopHealthButton.IsEnabled = state.IsRunning;
        RemoveFailedButton.IsEnabled = !state.IsRunning && _rows.Any(row => row.HealthState == ProxyHealthState.Unreachable);
        SetStatus(state.Status);
    }

    private void BanButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(row => row.Selected).Select(row => row.Endpoint).ToArray();
        if (selected.Length == 0)
        {
            SetStatus("Select at least one proxy to ban.");
            return;
        }
        try
        {
            _store.Ban(selected);
            Reload();
            SetStatus($"Banned {selected.Length} proxies.");
        }
        catch (Exception ex)
        {
            SetStatus("Ban failed: " + ex.Message);
        }
    }

    private void RemoveFailedButton_Click(object sender, RoutedEventArgs e)
    {
        var failed = _rows.Where(row => row.HealthState == ProxyHealthState.Unreachable)
            .Select(row => row.Endpoint).ToArray();
        if (failed.Length == 0)
        {
            SetStatus("No failed proxy diagnostics to remove.");
            return;
        }
        try
        {
            _store.RemoveFromActive(failed);
            Reload();
            SetStatus($"Removed {failed.Length} failed proxies from the active pool.");
        }
        catch (Exception ex)
        {
            SetStatus("Remove failed proxies failed: " + ex.Message);
        }
    }

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
        _healthCoordinator.StateChanged -= HealthCoordinator_StateChanged;
        _healthCoordinator.HealthSaved -= HealthCoordinator_HealthSaved;
    }
}
