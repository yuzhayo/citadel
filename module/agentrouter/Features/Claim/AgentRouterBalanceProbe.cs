using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Module.Agentrouter.Features.Claim;

/// <summary>What one balance probe observed.</summary>
internal enum BalanceProbeState
{
    /// <summary>HTTP call or payload was not usable.</summary>
    Unreachable,

    /// <summary>The stored credential was rejected.</summary>
    Rejected,

    /// <summary>Quota was read.</summary>
    Ok,
}

internal sealed record BalanceProbeResult(
    BalanceProbeState State,
    long Quota,
    long UsedQuota,
    string Detail)
{
    /// <summary>
    /// Gateway quota units per one unit of account currency. Both the balance
    /// and the consumption figure the dashboard shows are this division.
    /// </summary>
    public const long QuotaPerUnit = 500_000;

    public static BalanceProbeResult Fail(BalanceProbeState state, string detail)
        => new(state, 0, 0, detail);

    public decimal Balance => (decimal)Quota / QuotaPerUnit;

    public decimal Consumption => (decimal)UsedQuota / QuotaPerUnit;
}

/// <summary>
/// Reads an account's own quota from the gateway.
///
/// <para><c>GET /api/user/self</c> answers the System Access Token the claim
/// flow captured (the profile JSON's <c>pat</c>) sent as
/// <c>Authorization: Bearer</c>, together with the account's numeric id in the
/// <c>New-API-User</c> header. That pair is the only credential that outlives a
/// claim run: the flow ends logged out, so a session cookie is gone by the time
/// this screen asks.</para>
///
/// <para>Nothing here parses the console page or drives a browser — one
/// authenticated GET — which is why a check costs no browser and no profile
/// lock.</para>
/// </summary>
internal sealed class AgentRouterBalanceProbe
{
    private const string SelfEndpoint = "/api/user/self";

    // The site sits behind a WAF that answers a bare client; the request shape
    // that was proven against the live gateway carries a browser UA.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        + " (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _baseAddress;

    public AgentRouterBalanceProbe(HttpClient http, string baseAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseAddress = baseAddress.TrimEnd('/');
    }

    public async Task<BalanceProbeResult> ProbeAsync(
        string pat, long userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pat) || userId <= 0)
        {
            return BalanceProbeResult.Fail(
                BalanceProbeState.Rejected, "no pat or user id captured");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _baseAddress + SelfEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        request.Headers.Add("New-API-User", userId.ToString(
            CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return BalanceProbeResult.Fail(BalanceProbeState.Unreachable, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BalanceProbeResult.Fail(BalanceProbeState.Unreachable, "probe timed out");
        }

        using (response)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return BalanceProbeResult.Fail(
                    BalanceProbeState.Rejected,
                    $"credential rejected ({(int)response.StatusCode})");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BalanceProbeResult.Fail(
                    BalanceProbeState.Unreachable,
                    $"HTTP {(int)response.StatusCode}");
            }

            try
            {
                var payload = await response.Content
                    .ReadFromJsonAsync<Envelope>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (payload is null || !payload.Success || payload.Data is null)
                {
                    return BalanceProbeResult.Fail(
                        BalanceProbeState.Unreachable,
                        string.IsNullOrEmpty(payload?.Message)
                            ? "unexpected payload"
                            : payload!.Message!);
                }

                return new BalanceProbeResult(
                    BalanceProbeState.Ok,
                    payload.Data.Quota,
                    payload.Data.UsedQuota,
                    "ok");
            }
            catch (JsonException ex)
            {
                return BalanceProbeResult.Fail(BalanceProbeState.Unreachable, ex.Message);
            }
        }
    }

    private sealed record Envelope(
        bool Success,
        string? Message,
        SelfData? Data);

    /// <summary>
    /// The account-state payload names the consumption figure <c>used_quota</c>;
    /// the camel-case policy cannot map that onto UsedQuota by itself, so the
    /// name is pinned here.
    /// </summary>
    private sealed record SelfData(
        long Quota,
        [property: JsonPropertyName("used_quota")] long UsedQuota);
}
