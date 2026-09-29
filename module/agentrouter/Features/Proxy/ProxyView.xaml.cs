using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CitadelBridge;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Proxy;

/// <summary>
/// The Proxy tab: a read-only mirror of the pool the Proxy citizen committed.
/// It re-reads the producer's output on demand and presents health and
/// provenance; it holds no routing policy and writes nothing back.
/// </summary>
public partial class ProxyView : UserControl, IDisposable
{
    private readonly AgentProxyPool _pool;
    private readonly ObservableCollection<ProxyPoolRow> _rows = [];
    private bool _disposed;

    internal ProxyView(AgentProxyPool pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        InitializeComponent();
        PoolTable.ItemsSource = _rows;
        Reload();
    }

    /// <summary>Re-reads the committed pool and its sidecars.</summary>
    internal void Reload()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _pool.Reload();

            _rows.Clear();
            foreach (var row in _pool.Rows)
            {
                _rows.Add(new ProxyPoolRow(row));
            }

            var healthy = _rows.Count(row => row.HealthState == ProxyHealthState.Healthy);
            var failed = _rows.Count(row => row.HealthState == ProxyHealthState.Unreachable);
            SummaryText.Text =
                $"Committed pool: {_rows.Count} proxies · {healthy} healthy · {failed} failed";

            EmptyText.Visibility = _rows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            SetStatus(_pool.SkippedLines > 0
                ? $"Skipped {_pool.SkippedLines} malformed lines while reading the pool."
                : null);
        }
        catch (Exception ex)
        {
            SetStatus("Pool load failed: " + ex.Message);
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => Reload();

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
