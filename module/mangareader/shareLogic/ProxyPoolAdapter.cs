using System.IO;
using CitadelBridge;

namespace Module.Mangareader.ShareLogic;

internal enum ProxyTarget
{
    Browser,
    Http,
}

internal sealed record ProxyLease(ProxyEndpoint Endpoint)
{
    public ProxyLaunchOptions ToLaunchOptions() =>
        new(Endpoint.Server, Endpoint.Username, Endpoint.Password);
}

/// <summary>One lane's read-only pool cursor and transient quarantine.</summary>
internal sealed class ProxyPoolAdapter(string lane, string? poolPath = null)
{
    private static readonly HashSet<string> BrowserSchemes = new(
        ["http", "https", "socks5"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HttpSchemes = new(
        ["http", "https"], StringComparer.OrdinalIgnoreCase);
    private readonly string _lane = string.IsNullOrWhiteSpace(lane)
        ? throw new ArgumentException("lane is required", nameof(lane))
        : lane;
    private readonly string _poolPath = poolPath ?? ProxyPoolContract.ActivePoolPath;
    private readonly object _gate = new();
    private ProxyPoolSnapshot _snapshot = new([], long.MinValue, 0);
    private readonly HashSet<string> _quarantine = new(StringComparer.Ordinal);
    private int _browserCursor;
    private int _httpCursor;
    private bool _enabled;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) _enabled = value; }
    }

    public bool IsProxyMode => Enabled;

    internal ProxyLeaseRegistry Reservations { get; } = new();

    internal IReadOnlyList<ProxyEndpoint> Candidates(ProxyTarget target)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var schemes = target == ProxyTarget.Browser ? BrowserSchemes : HttpSchemes;
            return _snapshot.Endpoints.Where(item => schemes.Contains(item.Scheme))
                .DistinctBy(ProxyLeaseRegistry.Key).ToArray();
        }
    }

    public ProxyLease? Acquire(ProxyTarget target) => Acquire(target, IsProxyMode);

    internal ProxyLease? Acquire(ProxyTarget target, bool useProxy)
    {
        lock (_gate)
        {
            if (!useProxy) return null;
            ReloadIfChanged();
            if (_snapshot.Endpoints.Length == 0)
            {
                throw Error("PROXY_POOL_EMPTY", "proxy.txt is empty or missing");
            }

            var schemes = target == ProxyTarget.Browser ? BrowserSchemes : HttpSchemes;
            var compatible = _snapshot.Endpoints.Where(item => schemes.Contains(item.Scheme)).ToArray();
            if (compatible.Length == 0)
            {
                throw Error("PROXY_POOL_INCOMPATIBLE", $"no {target.ToString().ToLowerInvariant()}-compatible endpoint");
            }

            var cursor = target == ProxyTarget.Browser ? _browserCursor : _httpCursor;
            for (var offset = 0; offset < compatible.Length; offset++)
            {
                var index = (cursor + offset) % compatible.Length;
                var endpoint = compatible[index];
                if (_quarantine.Contains(endpoint.Canonical)) continue;
                if (target == ProxyTarget.Browser) _browserCursor = (index + 1) % compatible.Length;
                else _httpCursor = (index + 1) % compatible.Length;
                return new ProxyLease(endpoint);
            }
            throw Error("PROXY_POOL_EXHAUSTED", "every compatible endpoint is quarantined for this pool revision");
        }
    }

    public void ReportFailure(ProxyLease lease)
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
        _browserCursor = 0;
        _httpCursor = 0;
    }

    private ProxyPoolException Error(string code, string detail) =>
        new(code, $"{_lane} proxy mode: {detail}.");
}
