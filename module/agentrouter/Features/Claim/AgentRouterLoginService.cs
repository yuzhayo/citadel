using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CitadelBridge;

namespace Module.Agentrouter.Features.Claim;

public sealed record LoginCheckOutcome(string State, string Detail)
{
    public string Describe() => State == "saved" ? Detail : "Login check: " + Detail;
}

/// <summary>Reads today's sign-in log and stores its result per profile.</summary>
internal sealed class AgentRouterLoginService : IDisposable
{
    private const string BaseAddress = "https://agentrouter.org";
    private const int MaxProxyAttempts = 10;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly AgentRouterRunProfileStore _profiles = new();
    private int _disposed;

    public async Task<LoginCheckOutcome> CheckAsync(
        string profileId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var profile = _profiles.Read(profileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Pat)
            || profile.UserId is not { } userId)
        {
            return new LoginCheckOutcome("refused", "PAT/user_id belum tersedia");
        }

        var localNow = DateTimeOffset.Now;
        var localDate = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = new DateTimeOffset(localNow.Date, localNow.Offset).ToUnixTimeSeconds();
        var end = localNow.ToUnixTimeSeconds();
        var endpoints = ProxyPoolContract.ReadSnapshot().Endpoints
            .Where(endpoint => endpoint.Scheme is "http" or "https")
            .OrderBy(_ => Random.Shared.Next())
            .Take(MaxProxyAttempts)
            .ToArray();

        LoginLookup? lookup = null;
        foreach (var endpoint in endpoints)
        {
            lookup = await RequestAsync(profile.Pat, userId, localDate, start, end,
                endpoint, cancellationToken).ConfigureAwait(false);
            if (lookup.IsUsable)
            {
                break;
            }
        }

        if (lookup is null || !lookup.IsUsable)
        {
            lookup = await RequestAsync(profile.Pat, userId, localDate, start, end,
                null, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = new LoginSnapshot(
            lookup.Success,
            lookup.Content,
            lookup.Detail,
            localDate,
            DateTimeOffset.UtcNow);
        if (!_profiles.WriteLogin(profileId, snapshot))
        {
            return new LoginCheckOutcome("failed", "hasil dibaca tetapi gagal disimpan ke JSON");
        }

        return new LoginCheckOutcome(
            "saved",
            lookup.Success ? "✓ Login" : "✗ Login");
    }

    private static async Task<LoginLookup> RequestAsync(
        string pat, long userId, string localDate, long start, long end,
        ProxyEndpoint? proxy, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler();
        if (proxy is not null)
        {
            handler.Proxy = new WebProxy(proxy.Canonical);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        var uri = $"{BaseAddress}/api/log/self?p=1&page_size=100&type=0"
            + "&token_name=&model_name="
            + $"&start_timestamp={start.ToString(CultureInfo.InvariantCulture)}"
            + $"&end_timestamp={end.ToString(CultureInfo.InvariantCulture)}&group=";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        request.Headers.Add("New-API-User", userId.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "Chrome/154.0.0.0 Safari/537.36");

        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return LoginLookup.Failed($"HTTP {(int)response.StatusCode}");
            }

            var parsed = JsonNode.Parse(body) as JsonObject;
            if (parsed is null || parsed["success"]?.GetValue<bool>() != true)
            {
                return LoginLookup.Failed("respons bukan JSON login yang valid");
            }

            var items = (parsed["data"]?["items"] as JsonArray) ?? [];
            var content = items
                .OfType<JsonObject>()
                .Select(item => item["content"]?.GetValue<string>())
                .FirstOrDefault(text => text?.Contains("签到", StringComparison.Ordinal) == true);
            return LoginLookup.Successful(content);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LoginLookup.Failed("timeout 3 detik");
        }
        catch (HttpRequestException ex)
        {
            return LoginLookup.Failed(ex.Message);
        }
        catch (JsonException)
        {
            return LoginLookup.Failed("respons bukan JSON, kemungkinan WAF");
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private sealed record LoginLookup(bool IsUsable, bool Success, string? Content, string Detail)
    {
        public static LoginLookup Successful(string? content)
            => new(true, content is not null, content,
                content is not null ? "✓ Login" : "✗ Login");

        public static LoginLookup Failed(string detail) => new(false, false, null, detail);
    }
}
