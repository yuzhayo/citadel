using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Module.Yuzvid.Features.Browser;

internal enum BrowserDnsMode
{
    System,
    Cloudflare,
    Google,
}

/// <summary>
/// Resolves direct browser connections. Secure modes use the selected public
/// DNS-over-HTTPS provider; System leaves lookup to Windows/network DNS.
/// </summary>
internal sealed class SecureDnsResolver
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public async Task<IPAddress[]> ResolveAsync(string host, BrowserDnsMode mode, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];

        if (mode == BrowserDnsMode.System)
            return await Dns.GetHostAddressesAsync(host, token);

        var endpoint = mode == BrowserDnsMode.Cloudflare
            ? "https://cloudflare-dns.com/dns-query?name="
            : "https://dns.google/resolve?name=";

        using var request = new HttpRequestMessage(HttpMethod.Get,
            endpoint + Uri.EscapeDataString(host) + "&type=A");
        request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
        var addresses = new List<IPAddress>();
        if (document.RootElement.TryGetProperty("Answer", out var answers))
        {
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("data", out var data)
                    && IPAddress.TryParse(data.GetString(), out var address))
                {
                    addresses.Add(address);
                }
            }
        }

        if (addresses.Count == 0)
            throw new InvalidOperationException($"{mode} Secure DNS returned no address for {host}.");
        return addresses.ToArray();
    }
}
