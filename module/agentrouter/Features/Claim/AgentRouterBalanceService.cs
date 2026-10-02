using System.Globalization;
using System.Net.Http;

namespace Module.Agentrouter.SharedLogic;

/// <summary>
/// What one row reads from disk: the account id a claim recorded, the api_key
/// it captured, and the stored balance.
/// </summary>
internal sealed record ProfileBalanceState(
    long? UserId,
    string ApiKey,
    BalanceSnapshot? Balance);

/// <summary>Hasil satu get balance. Tidak pernah memuat secret.</summary>
internal sealed record BalanceCheckOutcome(
    string State,
    string Detail,
    decimal Balance,
    decimal Used)
{
    /// <summary>One line for the status area.</summary>
    public string Describe() => State == "saved"
        ? $"Balance {Format(Balance)} USD · used {Format(Used)} USD"
        : Detail;

    // Invariant: the gateway quotes USD, and a locale-aware render would show
    // "77,26" to a comma-decimal reader.
    private static string Format(decimal value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// Fetches one profile's balance and stores it in that profile's run JSON.
///
/// <para>The get is ONE authenticated HTTP call with the credentials the claim
/// flow already captured (the panel PAT plus the account id) — no browser, no
/// profile lock, no logout, so it is safe to repeat and safe to run while a
/// claim is in flight on another profile.</para>
///
/// <para>A failed get is reported and NOT stored: the number on screen is the
/// last thing the gateway actually said, and a timeout says nothing about it.
/// Only a successful read updates the JSON.</para>
/// </summary>
internal sealed class AgentRouterBalanceService : IDisposable
{
    private const string BaseAddress = "https://agentrouter.org";

    // Short on purpose: this is a single small GET, and the caller is a button
    // click that must come back with an answer.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    private readonly AgentRouterRunProfileStore _profiles;
    private readonly HttpClient _http;
    private readonly AgentRouterBalanceProbe _probe;
    private int _disposed;

    public AgentRouterBalanceService(string? runsRoot = null)
    {
        _profiles = new AgentRouterRunProfileStore(runsRoot);
        _http = new HttpClient { Timeout = RequestTimeout };
        _probe = new AgentRouterBalanceProbe(_http, BaseAddress);
    }

    /// <summary>Reads the stored state for one row. Touches no network.</summary>
    public ProfileBalanceState Read(string profileId)
    {
        var profile = _profiles.Read(profileId);
        return new ProfileBalanceState(
            profile?.UserId,
            profile?.ApiKey ?? string.Empty,
            profile?.Balance);
    }

    /// <summary>Fetches the balance and, on success, writes it to the JSON.</summary>
    public async Task<BalanceCheckOutcome> CheckAsync(
        string profileId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profile = _profiles.Read(profileId);
        if (profile is null)
        {
            return new BalanceCheckOutcome(
                "refused",
                "no credentials stored for this profile — Claim it first",
                0,
                0);
        }

        if (profile.UserId is not { } userId)
        {
            // Captured before the flow started recording the chip id; one more
            // Claim fills it in without re-capturing api_key or pat.
            return new BalanceCheckOutcome(
                "refused",
                "this profile has no user id yet — Claim it once to record it",
                0,
                0);
        }

        var probe = await _probe
            .ProbeAsync(profile.Pat, userId, cancellationToken)
            .ConfigureAwait(false);

        if (probe.State != BalanceProbeState.Ok)
        {
            return new BalanceCheckOutcome(
                probe.State == BalanceProbeState.Rejected
                    ? "rejected"
                    : "unreachable",
                probe.State == BalanceProbeState.Rejected
                    ? "Balance refused: " + probe.Detail
                    : "Balance unreachable: " + probe.Detail,
                0,
                0);
        }

        var snapshot = new BalanceSnapshot(
            probe.Quota,
            probe.UsedQuota,
            probe.Balance,
            probe.Consumption,
            DateTimeOffset.UtcNow);

        if (!_profiles.WriteBalance(profileId, snapshot))
        {
            return new BalanceCheckOutcome(
                "failed",
                "Balance read but not saved: " + _profiles.PathFor(profileId),
                0,
                0);
        }

        return new BalanceCheckOutcome(
            "saved", "ok", snapshot.Usd, snapshot.UsedUsd);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _http.Dispose();
    }
}