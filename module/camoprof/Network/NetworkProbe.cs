using System.Net.NetworkInformation;
using System.Net.Http;

namespace Module.Camoprof.Network;

internal sealed class NetworkProbe : IDisposable
{
    private static readonly Uri MicrosoftProbe =
        new("https://www.msftconnecttest.com/connecttest.txt");
    private static readonly Uri CloudflareProbe =
        new("https://www.cloudflare.com/cdn-cgi/trace");
    private static readonly Uri GoogleProbe =
        new("https://accounts.google.com/");

    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(4),
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    public async Task<NetworkSample> SampleAsync(CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return new NetworkSample(false, false, DateTimeOffset.Now);
        }

        var microsoft = CanReachAsync(MicrosoftProbe, cancellationToken);
        var cloudflare = CanReachAsync(CloudflareProbe, cancellationToken);
        var google = CanReachAsync(GoogleProbe, cancellationToken);
        await Task.WhenAll(microsoft, cloudflare, google);

        return new NetworkSample(
            microsoft.Result || cloudflare.Result,
            google.Result,
            DateTimeOffset.Now);
    }

    public void Dispose() => _client.Dispose();

    private async Task<bool> CanReachAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode < 500;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or OperationCanceledException
                or ObjectDisposedException)
        {
            return false;
        }
    }
}
