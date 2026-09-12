using System.Text.Json.Serialization;

namespace Module.Proxy.SharedLogic;

internal sealed record ProxySettings(
    int TargetUsableEntries = 300,
    int SourceRequestTimeoutSeconds = 15,
    [property: JsonPropertyName("TcpConnectTimeoutSeconds")]
    int ProxyValidationTimeoutSeconds = 5,
    int ParallelTcpChecks = 32)
{
    public const int MaxTargetUsableEntries = 5000;

    public ProxySettings Validate() => this with
    {
        TargetUsableEntries = Math.Clamp(TargetUsableEntries, 1, MaxTargetUsableEntries),
        SourceRequestTimeoutSeconds = Math.Clamp(SourceRequestTimeoutSeconds, 3, 120),
        ProxyValidationTimeoutSeconds = Math.Clamp(ProxyValidationTimeoutSeconds, 1, 30),
        ParallelTcpChecks = Math.Clamp(ParallelTcpChecks, 1, 128),
    };
}
