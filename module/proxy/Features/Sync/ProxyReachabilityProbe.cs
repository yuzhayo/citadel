using System.Net;
using System.Net.Http;
using CitadelBridge;

namespace Module.Proxy.Features.Sync;

internal interface IProxyReachabilityProbe
{
    Task<bool> IsReachableAsync(
        ProxyEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ProxyReachabilityProbe : IProxyReachabilityProbe
{
    private static readonly Uri ProbeUri = new("https://example.com/", UriKind.Absolute);

    public async Task<bool> IsReachableAsync(
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
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProbeUri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Citadel/ProxyValidation");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancellation.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
