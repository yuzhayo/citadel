using System.IO;
using CitadelBridge;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// Read-only proxy pool adapter for the Yuzvid browser. Reads from the shared
/// Citadel proxy pool (same file MangaReader uses) with poll-based reload.
/// 
/// One adapter instance per browser session. Provides the current proxy endpoint
/// for WebView2 environment creation. When the proxy changes, the browser must
/// be destroyed and recreated with the new endpoint.
/// </summary>
internal sealed class YuzvidProxyPoolAdapter
{
    private static readonly HashSet<string> BrowserSchemes = new(
        ["http", "https", "socks5"], StringComparer.OrdinalIgnoreCase);

    private readonly string _poolPath;
    private readonly object _gate = new();
    private ProxyPoolSnapshot _snapshot = new([], long.MinValue, 0);
    private ProxyHealthSnapshot _health = ProxyHealthSnapshot.Empty;
    private long _healthRevision = long.MinValue;
    private readonly HashSet<string> _quarantine = new(StringComparer.Ordinal);
    private int _cursor;
    private bool _enabled;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) _enabled = value; }
    }

    public bool IsProxyMode => Enabled;

    public YuzvidProxyPoolAdapter(string? poolPath = null)
    {
        _poolPath = poolPath ?? ProxyPoolContract.ActivePoolPath;
    }

    /// <summary>
    /// Acquires the next available proxy endpoint for browser use.
    /// Returns null if proxy is disabled or pool is empty.
    /// Throws if proxy is enabled but no compatible endpoint is available.
    /// </summary>
    public ProxyEndpoint? Acquire()
    {
        lock (_gate)
        {
            if (!_enabled) return null;
            ReloadIfChanged();

            if (_snapshot.Endpoints.Length == 0)
                throw new ProxyPoolException("PROXY_POOL_EMPTY", "yuzvid browser: proxy.txt is empty or missing.");

            var compatible = _snapshot.Endpoints
                .Where(ep => BrowserSchemes.Contains(ep.Scheme))
                .Where(ep => HealthRank(ep) < 2)
                .Where(ep => !_quarantine.Contains(ep.Canonical))
                .ToArray();

            if (compatible.Length == 0)
                throw new ProxyPoolException("PROXY_POOL_EXHAUSTED", "yuzvid browser: every compatible endpoint is quarantined.");

            var endpoint = compatible[_cursor % compatible.Length];
            _cursor = (_cursor + 1) % compatible.Length;
            return endpoint;
        }
    }

    /// <summary>
    /// Reports a proxy failure and quarantines the endpoint.
    /// Call when the browser fails to connect through this proxy.
    /// </summary>
    public void ReportFailure(ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate) _quarantine.Add(endpoint.Canonical);
    }

    /// <summary>
    /// Clears the quarantine so previously-failed endpoints can be retried.
    /// </summary>
    public void ClearQuarantine()
    {
        lock (_gate) _quarantine.Clear();
    }

    /// <summary>
    /// Returns the count of available (non-quarantined, healthy) browser-compatible endpoints.
    /// </summary>
    public int AvailableCount
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChanged();
                return _snapshot.Endpoints
                    .Where(ep => BrowserSchemes.Contains(ep.Scheme))
                    .Where(ep => HealthRank(ep) < 2)
                    .Where(ep => !_quarantine.Contains(ep.Canonical))
                    .Count();
            }
        }
    }

    private void ReloadIfChanged()
    {
        var revision = File.Exists(_poolPath) ? File.GetLastWriteTimeUtc(_poolPath).Ticks : 0;
        var healthPath = ProxyPoolHealthContract.HealthPathFor(_poolPath);
        var healthRevision = File.Exists(healthPath) ? File.GetLastWriteTimeUtc(healthPath).Ticks : 0;

        if (revision == _snapshot.Revision && healthRevision == _healthRevision) return;

        if (revision != _snapshot.Revision)
        {
            _snapshot = ProxyPoolContract.ReadSnapshot(_poolPath);
            _quarantine.RemoveWhere(value => _snapshot.Endpoints.All(ep => ep.Canonical != value));
            _cursor = 0;
        }

        if (healthRevision != _healthRevision)
        {
            _health = ProxyPoolHealthContract.ReadSnapshot(healthPath);
            _healthRevision = healthRevision;
        }
    }

    private int HealthRank(ProxyEndpoint endpoint) =>
        _health.Entries.TryGetValue(ProxyPoolHealthContract.EndpointKey(endpoint), out var health)
            ? health.State switch
            {
                ProxyHealthState.Healthy => 0,
                ProxyHealthState.Unknown => 1,
                _ => 2,
            }
            : 1;
}
