using CitadelBridge;

namespace Module.Agentrouter.SharedLogic;

/// <summary>
/// One pool endpoint as Agentrouter sees it: the producer's committed endpoint
/// joined with its health and provenance sidecars.
/// </summary>
internal sealed record AgentProxyRow(
    ProxyEndpoint Endpoint,
    ProxyHealthState Health,
    long? LatencyMilliseconds,
    DateTimeOffset? CheckedAtUtc,
    string? Diagnostic,
    string? OriginAccountId);

/// <summary>
/// Read-only adapter over the Proxy citizen's committed pool.
///
/// module/proxy is the producer and the only writer of
/// %LocalAppData%\Citadel\Proxy\*. This screen compiles the three shared
/// contracts — location, syntax, health and provenance — and reads them; it
/// never calls Ban, Commit or RemoveFromActive, and it keeps any rejection of
/// an endpoint local to itself rather than announcing it back to the pool.
///
/// Routing policy (which endpoint an agent gets, which schemes are usable) is
/// deliberately absent until a consumer needs it. The shared contract owns
/// location and syntax but no routing policy, and neither does this adapter.
/// </summary>
internal sealed class AgentProxyPool
{
    private readonly object _sync = new();
    private IReadOnlyList<AgentProxyRow> _rows = [];
    private int _skippedLines;
    private long _revision;

    public IReadOnlyList<AgentProxyRow> Rows
    {
        get
        {
            lock (_sync)
            {
                return _rows;
            }
        }
    }

    /// <summary>Malformed lines the producer's file had, as reported by the contract.</summary>
    public int SkippedLines
    {
        get
        {
            lock (_sync)
            {
                return _skippedLines;
            }
        }
    }

    /// <summary>Producer's last commit time, in ticks; 0 when no pool exists yet.</summary>
    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    public void Reload()
    {
        // Pool first: the origins sidecar is fingerprinted against it, and the
        // contract rejects it as stale the moment the committed pool changes.
        var pool = ProxyPoolContract.ReadSnapshot();
        var health = ProxyPoolHealthContract.ReadSnapshot();
        var origins = ProxyPoolOriginContract.ReadSnapshot(pool);

        var rows = new List<AgentProxyRow>(pool.Endpoints.Length);
        foreach (var endpoint in pool.Endpoints)
        {
            var key = ProxyPoolHealthContract.EndpointKey(endpoint);
            health.Entries.TryGetValue(key, out var record);
            origins.Entries.TryGetValue(key, out var origin);
            rows.Add(new AgentProxyRow(
                endpoint,
                record?.State ?? ProxyHealthState.Unknown,
                record?.LatencyMilliseconds,
                record?.CheckedAtUtc,
                record?.Detail,
                origin?.AccountId));
        }

        lock (_sync)
        {
            _rows = rows;
            _skippedLines = pool.SkippedLines;
            _revision = pool.Revision;
        }
    }
}
