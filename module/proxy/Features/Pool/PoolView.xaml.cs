using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CitadelBridge;
using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Pool;

internal sealed class ProxyPoolRow(ProxyEndpoint endpoint)
{
    public ProxyEndpoint Endpoint { get; } = endpoint;
    public bool Selected { get; set; }
    public string Masked => Endpoint.Masked;
    public string Scheme => Endpoint.Scheme;
    public string Host => Endpoint.Host;
    public int Port => Endpoint.Port;
}

public partial class PoolView : UserControl
{
    private readonly ProxyPoolStore _store;
    private readonly ObservableCollection<ProxyPoolRow> _rows = [];

    internal PoolView(ProxyPoolStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        PoolTable.ItemsSource = _rows;
        Reload();
    }

    internal void Reload()
    {
        try
        {
            var snapshot = _store.LoadActive();
            _rows.Clear();
            foreach (var endpoint in snapshot.Endpoints)
            {
                _rows.Add(new ProxyPoolRow(endpoint));
            }
            SummaryText.Text = $"Committed pool: {_rows.Count} proxies";
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BanButton.IsEnabled = _rows.Count > 0;
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

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
