using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly AgentRouterRunProfileStore _profiles;
    private readonly AgentRouterProxyManager _proxyManager;
    private int _disposed;

    public AgentRouterLoginService(
        AgentRouterRunProfileStore profiles,
        AgentRouterProxyManager proxyManager)
    {
        _profiles = profiles;
        _proxyManager = proxyManager;
    }

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
        using var checkSlot = await _proxyManager
            .EnterCheckAsync(cancellationToken).ConfigureAwait(false);
        using var lease = await _proxyManager
            .AcquireAsync(cancellationToken).ConfigureAwait(false);

        LoginLookup? lookup = null;
        foreach (var endpoint in lease.Endpoints)
        {
            lookup = await RequestAsync(profile.Pat, userId, localDate, start, end,
                endpoint, cancellationToken).ConfigureAwait(false);
            if (lookup.ProxyUsable)
            {
                lease.MarkSuccess(endpoint);
                break;
            }
            lease.MarkFailure(endpoint);
        }

        if (lookup is null || !lookup.ProxyUsable)
        {
            lookup = await RequestAsync(profile.Pat, userId, localDate, start, end,
                null, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = new LoginSnapshot(
            lookup.Success,
            lookup.Verified,
            lookup.Content,
            lookup.Detail,
            localDate,
            DateTimeOffset.UtcNow,
            lookup.RewardUsd);
        if (!_profiles.WriteLogin(profileId, snapshot))
        {
            return new LoginCheckOutcome("failed", "hasil dibaca tetapi gagal disimpan ke JSON");
        }

        return new LoginCheckOutcome("saved", lookup.Detail);
    }

    private static async Task<LoginLookup> RequestAsync(
        string pat, long userId, string localDate, long start, long end,
        ProxyEndpoint? proxy, CancellationToken cancellationToken)
    {
        using var handler = AgentRouterProxyHttpHandler.Create(proxy);
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        var uri = $"{BaseAddress}/api/log/self?p=1&page_size=100&type=0"
            + "&token_name=&model_name="
            + $"&start_timestamp={start.ToString(CultureInfo.InvariantCulture)}"
            + $"&end_timestamp={end.ToString(CultureInfo.InvariantCulture)}&group=";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        request.Headers.Add("New-API-User", userId.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Referer", "https://agentrouter.org/");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
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
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not ("application/json" or "text/json")
                || body.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                return LoginLookup.Failed("respons HTML/WAF, bukan JSON");
            }

            var parsed = JsonNode.Parse(body) as JsonObject;
            if (parsed is null)
            {
                return LoginLookup.Failed("respons bukan JSON login yang valid");
            }

            if (parsed["success"]?.GetValue<bool>() != true)
            {
                var message = parsed["message"]?.GetValue<string>()
                    ?? "API menolak request";
                return LoginLookup.ApiRejected(message);
            }

            if (!response.IsSuccessStatusCode)
            {
                return LoginLookup.ApiRejected($"HTTP {(int)response.StatusCode}");
            }

            if (parsed["data"]?["items"] is not JsonArray items)
            {
                return LoginLookup.ApiRejected("respons API tidak berisi daftar log");
            }
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

    // The site's own check-in line, e.g. "每日签到成功，增加额度 ＄25.000000 额度",
    // where the amount varies. The dollar mark in it is the fullwidth U+FF04,
    // so it is matched beside the ASCII one; the number after it is what the
    // check reports as the granted quota.
    private static readonly Regex RewardPattern = new(
        @"[$\uFF04]\s*(\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static decimal? ParseRewardUsd(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var match = RewardPattern.Match(content);
        return match.Success
            && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string FormatReward(decimal value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed record LoginLookup(
        bool ProxyUsable,
        bool Verified,
        bool Success,
        string? Content,
        string Detail,
        decimal? RewardUsd)
    {
        public static LoginLookup Successful(string? content)
        {
            var reward = ParseRewardUsd(content);
            var detail = content is null
                ? "✗ Login"
                : reward is { } usd
                    ? $"✓ Login · +${FormatReward(usd)}"
                    : "✓ Login";
            return new(true, true, content is not null, content, detail, reward);
        }

        public static LoginLookup ApiRejected(string detail)
            => new(true, false, false, null, detail, null);

        public static LoginLookup Failed(string detail)
            => new(false, false, false, null, detail, null);
    }
}
