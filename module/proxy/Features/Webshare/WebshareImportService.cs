using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using CitadelBridge;
using Module.Proxy.Features.Sync;
using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Webshare;

internal sealed record WebshareProxy(string? Address, int Port, string? Username, string? Password, bool Valid);

internal sealed record WebshareFetchResult(IReadOnlyList<WebshareProxy> Proxies, int Skipped);

internal interface IWebshareApiClient
{
    Task<WebshareFetchResult> FetchAllAsync(string apiKey, string mode, CancellationToken cancellationToken);
}

/// <summary>Webshare's paginated list API. It keeps API keys in request headers only.</summary>
internal sealed class HttpWebshareApiClient(HttpClient client) : IWebshareApiClient
{
    private static readonly Uri ListUri = new("https://proxy.webshare.io/api/v2/proxy/list/", UriKind.Absolute);
    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<WebshareFetchResult> FetchAllAsync(string apiKey, string mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var next = new Uri(ListUri + $"?mode={mode}&page_size=100", UriKind.Absolute);
        var proxies = new List<WebshareProxy>();
        var skipped = 0;

        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.TryAddWithoutValidation("Authorization", "Token " + apiKey);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new WebshareApiException("Webshare rate limited this key; wait 60 seconds before processing it again.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new WebshareApiException($"Webshare API returned HTTP {(int)response.StatusCode}.");
            }

            var page = await JsonSerializer.DeserializeAsync<WebsharePage>(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new WebshareApiException("Webshare returned an empty page.");
            foreach (var item in page.Results ?? [])
            {
                if (!item.Valid)
                {
                    skipped++;
                    continue;
                }
                proxies.Add(new WebshareProxy(item.ProxyAddress, item.Port, item.Username, item.Password, item.Valid));
            }
            next = ParseNext(page.Next);
        }

        return new WebshareFetchResult(proxies, skipped);
    }

    private static Uri? ParseNext(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, ListUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebshareApiException("Webshare returned an invalid pagination URL.");
        }
        return uri;
    }

    private sealed record WebsharePage(
        [property: JsonPropertyName("next")] string? Next,
        [property: JsonPropertyName("results")] WebshareProxyDto[]? Results);

    private sealed record WebshareProxyDto(
        [property: JsonPropertyName("proxy_address")] string? ProxyAddress,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("valid")] bool Valid);
}

internal sealed class WebshareApiException(string message) : Exception(message);

internal sealed record WebshareImportProgress(
    bool IsRunning,
    string Status,
    int KeysCompleted = 0,
    int KeyCount = 0,
    int Candidates = 0,
    int Checked = 0,
    int Reachable = 0,
    int Failed = 0,
    TimeSpan? Elapsed = null);

internal sealed class WebshareImportService(
    IWebshareApiClient api,
    IProxyReachabilityProbe probe)
{
    private readonly IWebshareApiClient _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IProxyReachabilityProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<(IReadOnlyList<ProxyEndpoint> Reachable, IReadOnlyList<ProxyHealthRecord> Health, int Candidates, int Skipped)> RunAsync(
        IReadOnlyList<string> keys,
        ProxySettings settings,
        IReadOnlySet<string> banned,
        IProgress<WebshareImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0) throw new InvalidOperationException("Add at least one Webshare API key first.");
        settings = settings.Validate();
        var stopwatch = Stopwatch.StartNew();
        var fetched = 0;
        var fetchTasks = keys.Select(async key =>
        {
            var result = await _api.FetchAllAsync(key, settings.WebshareConnectionMode, cancellationToken).ConfigureAwait(false);
            var completed = Interlocked.Increment(ref fetched);
            progress?.Report(new WebshareImportProgress(true, $"Fetched key {completed}/{keys.Count}.", completed, keys.Count,
                Elapsed: stopwatch.Elapsed));
            return result;
        }).ToArray();
        var results = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        var skipped = results.Sum(result => result.Skipped);
        var candidates = new SortedDictionary<string, ProxyEndpoint>(StringComparer.Ordinal);
        foreach (var proxy in results.SelectMany(result => result.Proxies))
        {
            if (!TryMap(proxy, settings.WebshareConnectionMode, out var endpoint))
            {
                skipped++;
                continue;
            }
            if (!banned.Contains(endpoint.Canonical)) candidates.TryAdd(endpoint.Canonical, endpoint);
        }

        var endpoints = candidates.Values.ToArray();
        var health = new ProxyHealthRecord[endpoints.Length];
        var reachable = new List<ProxyEndpoint>();
        var gate = new object();
        var next = -1;
        var checkedCount = 0;
        var failed = 0;
        var workerCount = Math.Min(settings.ParallelTcpChecks, endpoints.Length);
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= endpoints.Length) return;
                var endpoint = endpoints[index];
                var result = await _probe.ProbeAsync(endpoint,
                    TimeSpan.FromSeconds(settings.ProxyValidationTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                health[index] = result.ToHealthRecord(endpoint, DateTimeOffset.UtcNow);
                int checkedNow;
                int reachableNow;
                int failedNow;
                lock (gate)
                {
                    checkedNow = ++checkedCount;
                    if (result.IsHealthy) reachable.Add(endpoint);
                    else failed++;
                    reachableNow = reachable.Count;
                    failedNow = failed;
                }
                progress?.Report(new WebshareImportProgress(true,
                    $"Checking {checkedNow}/{endpoints.Length} · {reachableNow} reachable · {failedNow} failed.",
                    keys.Count, keys.Count, endpoints.Length, checkedNow, reachableNow, failedNow, stopwatch.Elapsed));
            }
        }).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        return (reachable.OrderBy(endpoint => endpoint.Canonical, StringComparer.Ordinal).ToArray(),
            health.OrderBy(record => record.EndpointKey, StringComparer.Ordinal).ToArray(), endpoints.Length, skipped);
    }

    internal static bool TryMap(WebshareProxy proxy, string mode, out ProxyEndpoint endpoint)
    {
        endpoint = default!;
        if (!proxy.Valid || proxy.Port is < 1 or > 65535) return false;
        var host = string.Equals(mode, "backbone", StringComparison.OrdinalIgnoreCase)
            ? "p.webshare.io"
            : proxy.Address?.Trim();
        if (string.IsNullOrWhiteSpace(host)) return false;
        var auth = string.IsNullOrWhiteSpace(proxy.Username)
            ? string.Empty
            : Uri.EscapeDataString(proxy.Username) + ":" + Uri.EscapeDataString(proxy.Password ?? string.Empty) + "@";
        return ProxyPoolContract.TryParse($"http://{auth}{host}:{proxy.Port}", null, out endpoint);
    }
}
