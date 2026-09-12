using System.Text.Json.Serialization;

namespace Module.Proxy.SharedLogic;

internal sealed record ProxySettings(
    int SourceRequestTimeoutSeconds = 15,
    [property: JsonPropertyName("TcpConnectTimeoutSeconds")]
    int ProxyValidationTimeoutSeconds = 5,
    int ParallelTcpChecks = 32,
    string WebshareConnectionMode = "backbone")
{
    public ProxySettings Validate() => this with
    {
        SourceRequestTimeoutSeconds = Math.Clamp(SourceRequestTimeoutSeconds, 3, 120),
        ProxyValidationTimeoutSeconds = Math.Clamp(ProxyValidationTimeoutSeconds, 1, 30),
        ParallelTcpChecks = Math.Clamp(ParallelTcpChecks, 1, 128),
        WebshareConnectionMode = string.Equals(WebshareConnectionMode, "direct", StringComparison.OrdinalIgnoreCase)
            ? "direct"
            : "backbone",
    };
}
