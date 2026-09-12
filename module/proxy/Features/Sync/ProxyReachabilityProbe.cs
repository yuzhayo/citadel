using System.Net;
using System.Net.Http;
using System.Diagnostics;
using CitadelBridge;

namespace Module.Proxy.Features.Sync;

internal interface IProxyReachabilityProbe
{
    Task<ProxyProbeResult> ProbeAsync(
        ProxyEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed record ProxyProbeResult(bool IsHealthy, long? LatencyMilliseconds, string Detail)
{
    public ProxyHealthRecord ToHealthRecord(ProxyEndpoint endpoint, DateTimeOffset checkedAtUtc) =>
        new(
            ProxyPoolHealthContract.EndpointKey(endpoint),
            IsHealthy ? ProxyHealthState.Healthy : ProxyHealthState.Unreachable,
            LatencyMilliseconds,
            checkedAtUtc,
            Detail);
}

internal sealed class ProxyReachabilityProbe : IProxyReachabilityProbe
{
    private static readonly Uri ProbeUri = new("https://example.com/", UriKind.Absolute);

    public async Task<ProxyProbeResult> ProbeAsync(
        ProxyEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var proxy = new WebProxy(endpoint.Server);
        if (endpoint.Username is not null)
        {
            proxy.Credentials = new NetworkCredential(
                endpoint.Username,
                endpoint.Password ?? string.Empty);
        }

        using var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = timeout,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProbeUri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Citadel/ProxyValidation");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancellation.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return response.IsSuccessStatusCode
                ? new ProxyProbeResult(true, Math.Max(1, (long)stopwatch.Elapsed.TotalMilliseconds), "OK")
                : new ProxyProbeResult(false, Math.Max(1, (long)stopwatch.Elapsed.TotalMilliseconds), $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyProbeResult(false, null, "Timeout");
        }
        catch (HttpRequestException)
        {
            return new ProxyProbeResult(false, null, "Transport failure");
        }
        catch (Exception)
        {
            return new ProxyProbeResult(false, null, "Probe failure");
        }
    }
}
