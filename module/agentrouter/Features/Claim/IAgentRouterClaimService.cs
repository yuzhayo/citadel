namespace Module.Agentrouter.Features.Claim;

/// <summary>Public boundary consumed by the Shortcuts UI.</summary>
public interface IAgentRouterClaimService : IDisposable
{
    Task<ClaimOutcome> ClaimAsync(string profileId, bool headless, string? proxy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claim, then read the account state the claim was supposed to produce.
    ///
    /// <para>The two follow-up reads are NOT gated on the claim outcome. A run
    /// that failed late - final quit, a stray overlay, a proxy dropping after
    /// the credentials were already written - still leaves <c>pat</c>,
    /// <c>user_id</c> and <c>api_key</c> in the profile JSON, and those are
    /// exactly what the checks read. Reporting only "Claim failed" would hide
    /// whether the login actually took and whether quota was granted.</para>
    ///
    /// <para>A stopped claim is the one exception: the account may still be
    /// mid-mutation, so neither read runs.</para>
    /// </summary>
    Task<ClaimVerifyOutcome> ClaimAndVerifyAsync(
        string profileId,
        bool headless,
        string? proxy,
        Func<bool> isStopRequested,
        CancellationToken cancellationToken = default);

    void StopClaim();

    ProfileBalanceState Read(string profileId);

    Task<BalanceCheckOutcome> CheckAsync(string profileId,
        CancellationToken cancellationToken = default);

    Task<LoginCheckOutcome> CheckLoginAsync(string profileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One claim plus whatever the follow-up reads could learn about it.
/// Renders as a single status line.
/// </summary>
public sealed record ClaimVerifyOutcome(
    ClaimOutcome Claim,
    bool ClaimUncertain,
    LoginCheckOutcome? Login,
    BalanceCheckOutcome? Balance,
    bool Stopped)
{
    public string Describe()
    {
        var parts = new List<string>(3)
        {
            Claim.Outcome switch
            {
                "saved" => Claim.Keys.Count > 0
                    ? "Claimed · keys: " + string.Join(", ", Claim.Keys)
                    : "Claimed",
                "busy" => "Claim busy",
                "refused" => "Claim refused: " + Claim.Detail,
                _ when ClaimUncertain => "Claim outcome unknown: " + Claim.Detail,
                _ => Claim.Detail.Length > 0
                    ? "Claim failed: " + Claim.Detail
                    : "Claim failed",
            },
        };

        if (Stopped)
        {
            parts.Add("stopped - verify the account before claiming again");
            return string.Join(" · ", parts);
        }

        if (Login is not null) parts.Add(Login.Describe());
        if (Balance is not null) parts.Add(Balance.Describe());

        return string.Join(" · ", parts);
    }
}

/// <summary>Claim and balance implementation owned by the Claim feature.</summary>
public sealed class AgentRouterClaimFeature : IAgentRouterClaimService
{
    private readonly AgentrouterClaimClient _claims = new();
    private readonly AgentRouterRunProfileStore _profiles = new();
    private readonly AgentRouterProxyManager _proxyManager = new();
    private readonly AgentRouterBalanceService _balances;
    private readonly AgentRouterLoginService _login;
    private int _disposed;

    public AgentRouterClaimFeature()
    {
        _balances = new AgentRouterBalanceService(_profiles, _proxyManager);
        _login = new AgentRouterLoginService(_profiles, _proxyManager);
    }

    public Task<ClaimOutcome> ClaimAsync(string profileId, bool headless,
        string? proxy, CancellationToken cancellationToken = default)
        => _claims.ClaimAsync(profileId, headless, proxy, cancellationToken);

    public async Task<ClaimVerifyOutcome> ClaimAndVerifyAsync(
        string profileId,
        bool headless,
        string? proxy,
        Func<bool> isStopRequested,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(isStopRequested);

        var claimUncertain = false;
        ClaimOutcome claim;
        try
        {
            claim = await _claims
                .ClaimAsync(profileId, headless, proxy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A thrown claim is an unknown outcome, not a clean failure: the
            // flow may already have written credentials or mutated the account.
            claimUncertain = true;
            claim = new ClaimOutcome("failed", 1, ex.Message, []);
        }

        if (isStopRequested())
        {
            return new ClaimVerifyOutcome(claim, claimUncertain, null, null, true);
        }

        // Deliberately not gated on Claim.Outcome - see the interface note.
        // Both calls are the SAME public methods the Check login / Check
        // balance buttons run - no separate check path exists.
        LoginCheckOutcome login;
        try
        {
            login = await CheckLoginAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            login = new LoginCheckOutcome("failed", ex.Message);
        }

        if (isStopRequested())
        {
            return new ClaimVerifyOutcome(claim, claimUncertain, login, null, true);
        }

        BalanceCheckOutcome balance;
        try
        {
            balance = await CheckAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            balance = new BalanceCheckOutcome("failed", ex.Message, 0, 0);
        }

        return new ClaimVerifyOutcome(
            claim, claimUncertain, login, balance, isStopRequested());
    }

    public void StopClaim() => _claims.StopClaim();

    public ProfileBalanceState Read(string profileId) => _balances.Read(profileId);

    public Task<BalanceCheckOutcome> CheckAsync(string profileId,
        CancellationToken cancellationToken = default)
        => _balances.CheckAsync(profileId, cancellationToken);

    public Task<LoginCheckOutcome> CheckLoginAsync(string profileId,
        CancellationToken cancellationToken = default)
        => _login.CheckAsync(profileId, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _claims.Dispose();
        _balances.Dispose();
        _login.Dispose();
    }
}
