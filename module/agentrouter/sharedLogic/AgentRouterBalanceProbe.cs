using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Module.Agentrouter.SharedLogic;

/// <summary>What one balance probe observed.</summary>
internal enum BalanceProbeState
{
    /// <summary>HTTP call or payload was not usable.</summary>
    Unreachable,

    /// <summary>The stored PAT was rejected.</summary>
    Rejected,

    /// <summary>Quota was read.</summary>
    Ok,
}

internal sealed record BalanceProbeResult(
    BalanceProbeState State,
    long Quota,
    long UsedQuota,
    string Pat,
    string Detail)
{
    /// <summary>
    /// Gateway quota units per one unit of account currency. Both the balance
    /// and the consumption figure the dashboard shows are this division.
    /// </summary>
    public const long QuotaPerUnit = 500_000;

    public static BalanceProbeResult Fail(BalanceProbeState state, string detail)
        => new(state, 0, 0, string.Empty, detail);

    public decimal Balance => (decimal)Quota / QuotaPerUnit;

    public decimal Consumption => (decimal)UsedQuota / QuotaPerUnit;
}

/// <summary>
/// Reads an account's own quota from the gateway.
///
/// <para><c>GET /api/user/self</c> authenticates with the browser session
/// cookie plus a <c>New-API-User</c> id header. A panel PAT presented as
/// <c>Authorization: Bearer</c> is refused with 401.</para>
///
/// <para>The response also carries <c>access_token</c>, so one call yields
/// both the quota and the account's System Access Token.</para>
///
/// This is the module's verification surface. The browser establishes the
/// session; this proves whether quota actually moved. Nothing here parses the
/// page or depends on the site's markup.
/// </summary>
internal sealed class AgentRouterBalanceProbe
{
    private const string SelfEndpoint = "/api/user/self";

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
        string session, long userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session) || userId <= 0)
        {
            return BalanceProbeResult.Fail(
                BalanceProbeState.Rejected, "no session cookie or user id stored");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _baseAddress + SelfEndpoint);
        request.Headers.Add("New-API-User", userId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("Cookie", $"session={session}");

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
                    $"PAT rejected ({(int)response.StatusCode})");
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
                    payload.Data.AccessToken ?? string.Empty,
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
    /// The self response carries the account's System Access Token in the same
    /// payload as the quota, so one authenticated call yields both.
    /// </summary>
    private sealed record SelfData(long Quota, long UsedQuota, string? AccessToken);
}