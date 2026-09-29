using CitadelBridge;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Proxy;

/// <summary>
/// One row of the read-only pool table: the producer's endpoint joined with
/// the health and provenance the Proxy citizen committed alongside it.
/// </summary>
internal sealed class ProxyPoolRow(AgentProxyRow row)
{
    public string EndpointText => row.Endpoint.Canonical;

    public string Scheme => row.Endpoint.Scheme;

    public string Host => row.Endpoint.Host;

    public int Port => row.Endpoint.Port;

    public string Health => row.Health switch
    {
        ProxyHealthState.Healthy => "Healthy",
        ProxyHealthState.Unreachable => "Failed",
        _ => "Unknown",
    };

    public ProxyHealthState HealthState => row.Health;

    public long HealthSort => row.Health switch
    {
        ProxyHealthState.Healthy => 0,
        ProxyHealthState.Unknown => 1,
        _ => 2,
    };

    public string Latency => row.LatencyMilliseconds is long value ? $"{value} ms" : "—";

    public long LatencySort => row.LatencyMilliseconds ?? long.MaxValue;

    public string Checked => row.CheckedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    public long CheckedSort => row.CheckedAtUtc?.UtcTicks ?? 0;

    public string Diagnostic => row.Diagnostic ?? "Not checked";

    public string Origin => row.OriginAccountId ?? "—";
}
