using System.IO;
using CitadelBridge;

namespace Module.Camoprof.SharedLogic;

internal sealed record CamoProxyLease(ProxyEndpoint Endpoint)
{
    public ProxyLaunchOptions ToLaunchOptions() =>
        new(Endpoint.Server, Endpoint.Username, Endpoint.Password);
}

internal sealed class ProxyPoolAdapter(string? poolPath = null)
{
    private static readonly HashSet<string> BrowserSchemes = new(
        ["http", "https", "socks5"], StringComparer.OrdinalIgnoreCase);
    private readonly string _poolPath = poolPath ?? ProxyPoolContract.ActivePoolPath;
    private readonly object _gate = new();
    private ProxyPoolSnapshot _snapshot = new([], long.MinValue, 0);
    private readonly HashSet<string> _quarantine = new(StringComparer.Ordinal);
    private int _cursor;
    private bool _enabled;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) _enabled = value; }
    }

    public CamoProxyLease? Acquire()
    {
        lock (_gate)
        {
            if (!_enabled) return null;
            ReloadIfChanged();
            if (_snapshot.Endpoints.Length == 0)
            {
                throw new ProxyPoolException("PROXY_POOL_EMPTY", "Proxy pool is enabled but proxy.txt is empty or missing.");
            }

            var compatible = _snapshot.Endpoints
                .Where(item => BrowserSchemes.Contains(item.Scheme))
                .ToArray();
            if (compatible.Length == 0)
            {
                throw new ProxyPoolException("PROXY_POOL_INCOMPATIBLE", "Proxy pool has no Camofox-compatible endpoint.");
            }

            for (var offset = 0; offset < compatible.Length; offset++)
            {
                var index = (_cursor + offset) % compatible.Length;
                var endpoint = compatible[index];
                if (_quarantine.Contains(endpoint.Canonical)) continue;
                _cursor = (index + 1) % compatible.Length;
                return new CamoProxyLease(endpoint);
            }
            throw new ProxyPoolException("PROXY_POOL_EXHAUSTED", "Every compatible proxy is quarantined for the current pool revision.");
        }
    }

    public void ReportFailure(CamoProxyLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate) _quarantine.Add(lease.Endpoint.Canonical);
    }

    private void ReloadIfChanged()
    {
        var revision = File.Exists(_poolPath) ? File.GetLastWriteTimeUtc(_poolPath).Ticks : 0;
        if (revision == _snapshot.Revision) return;
        _snapshot = ProxyPoolContract.ReadSnapshot(_poolPath);
        _quarantine.RemoveWhere(value => _snapshot.Endpoints.All(item => item.Canonical != value));
        _cursor = 0;
    }
}
