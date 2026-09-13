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

/// <summary>
/// One lane's read-only pool cursor and transient quarantine. Reservations may
/// be shared by several lanes, while their enablement and quarantine remain
/// intentionally local.
/// </summary>
internal sealed class ProxyPoolAdapter
{
    private static readonly HashSet<string> BrowserSchemes = new(
        ["http", "https", "socks5"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HttpSchemes = new(
        ["http", "https"], StringComparer.OrdinalIgnoreCase);
    private readonly string _lane;
    private readonly string _poolPath;
    private readonly object _gate = new();
    private ProxyPoolSnapshot _snapshot = new([], long.MinValue, 0);
    private ProxyHealthSnapshot _health = ProxyHealthSnapshot.Empty;
    private long _healthRevision = long.MinValue;
    private ProxyPoolOriginSnapshot _origins = ProxyPoolOriginSnapshot.Empty;
    private long _originRevision = long.MinValue;
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

    public ProxyPoolAdapter(string lane, string? poolPath = null)
        : this(lane, new ProxyLeaseRegistry(), poolPath)
    {
    }

    internal ProxyPoolAdapter(string lane, ProxyLeaseRegistry reservations, string? poolPath = null)
    {
        _lane = string.IsNullOrWhiteSpace(lane)
            ? throw new ArgumentException("lane is required", nameof(lane))
            : lane;
        Reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _poolPath = poolPath ?? ProxyPoolContract.ActivePoolPath;
    }

    internal ProxyLeaseRegistry Reservations { get; }

    internal Task<ProxyReservation> ReserveAsync(
        string owner,
        IReadOnlyList<ProxyEndpoint> candidates,
        CancellationToken token) =>
        Reservations.ReserveAsync(owner, candidates, token, AccountIdFor);

    internal IReadOnlyList<ProxyEndpoint> Candidates(ProxyTarget target)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var schemes = target == ProxyTarget.Browser ? BrowserSchemes : HttpSchemes;
            return OrderByHealth(_snapshot.Endpoints.Where(item => schemes.Contains(item.Scheme)))
                .Where(item => HealthRank(item) < 2)
                .DistinctBy(ProxyLeaseRegistry.Key).ToArray();
        }
    }

    /// <summary>
    /// Snapshot for a bounded parallel lane. Unlike <see cref="Candidates"/>,
    /// quarantined endpoints are excluded before the reservation registry
    /// assigns a worker, so a retry cannot immediately reclaim a known-bad
    /// proxy.
    /// </summary>
    internal IReadOnlyList<ProxyEndpoint> AvailableCandidates(ProxyTarget target)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var schemes = target == ProxyTarget.Browser ? BrowserSchemes : HttpSchemes;
            return OrderByHealth(_snapshot.Endpoints
                .Where(item => schemes.Contains(item.Scheme) && !_quarantine.Contains(item.Canonical)))
                .Where(item => HealthRank(item) < 2)
                .DistinctBy(ProxyLeaseRegistry.Key)
                .ToArray();
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
            var compatible = OrderByHealth(_snapshot.Endpoints.Where(item => schemes.Contains(item.Scheme)))
                .Where(item => HealthRank(item) < 2)
                .ToArray();
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
        var healthPath = ProxyPoolHealthContract.HealthPathFor(_poolPath);
        var originPath = ProxyPoolOriginContract.OriginsPathFor(_poolPath);
        var healthRevision = File.Exists(healthPath) ? File.GetLastWriteTimeUtc(healthPath).Ticks : 0;
        var originRevision = File.Exists(originPath) ? File.GetLastWriteTimeUtc(originPath).Ticks : 0;
        if (revision == _snapshot.Revision && healthRevision == _healthRevision && originRevision == _originRevision) return;
        var poolChanged = revision != _snapshot.Revision;
        if (poolChanged)
        {
            _snapshot = ProxyPoolContract.ReadSnapshot(_poolPath);
            _quarantine.RemoveWhere(value => _snapshot.Endpoints.All(item => item.Canonical != value));
            _browserCursor = 0;
            _httpCursor = 0;
        }
        if (healthRevision != _healthRevision)
        {
            _health = ProxyPoolHealthContract.ReadSnapshot(healthPath);
            _healthRevision = healthRevision;
        }
        if (poolChanged || originRevision != _originRevision)
        {
            _origins = ProxyPoolOriginContract.ReadSnapshot(_snapshot, originPath);
            _originRevision = originRevision;
        }
    }

    private string? AccountIdFor(ProxyEndpoint endpoint)
    {
        var key = ProxyPoolHealthContract.EndpointKey(endpoint);
        return _origins.Entries.TryGetValue(key, out var origin)
            && string.Equals(origin.Source, "webshare", StringComparison.Ordinal)
            ? origin.AccountId
            : null;
    }

    private IEnumerable<ProxyEndpoint> OrderByHealth(IEnumerable<ProxyEndpoint> endpoints) =>
        endpoints.OrderBy(endpoint => HealthRank(endpoint))
            .ThenBy(endpoint => HealthLatency(endpoint))
            .ThenBy(endpoint => endpoint.Canonical, StringComparer.Ordinal);

    private int HealthRank(ProxyEndpoint endpoint) =>
        _health.Entries.TryGetValue(ProxyPoolHealthContract.EndpointKey(endpoint), out var health)
            ? health.State switch
            {
                ProxyHealthState.Healthy => 0,
                ProxyHealthState.Unknown => 1,
                _ => 2,
            }
            : 1;

    private long HealthLatency(ProxyEndpoint endpoint) =>
        _health.Entries.TryGetValue(ProxyPoolHealthContract.EndpointKey(endpoint), out var health)
            ? health.LatencyMilliseconds ?? long.MaxValue
            : long.MaxValue;

    private ProxyPoolException Error(string code, string detail) =>
        new(code, $"{_lane} proxy mode: {detail}.");
}
