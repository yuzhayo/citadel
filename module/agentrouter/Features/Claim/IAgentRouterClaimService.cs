namespace Module.Agentrouter.Features.Claim;

/// <summary>Public boundary consumed by the Shortcuts UI.</summary>
public interface IAgentRouterClaimService : IDisposable
{
    Task<ClaimOutcome> ClaimAsync(string profileId, bool headless, string? proxy,
        CancellationToken cancellationToken = default);

    ProfileBalanceState Read(string profileId);

    Task<BalanceCheckOutcome> CheckAsync(string profileId,
        CancellationToken cancellationToken = default);
}

/// <summary>Claim and balance implementation owned by the Claim feature.</summary>
public sealed class AgentRouterClaimFeature : IAgentRouterClaimService
{
    private readonly AgentrouterClaimClient _claims = new();
    private readonly AgentRouterBalanceService _balances = new();
    private int _disposed;

    public Task<ClaimOutcome> ClaimAsync(string profileId, bool headless,
        string? proxy, CancellationToken cancellationToken = default)
        => _claims.ClaimAsync(profileId, headless, proxy, cancellationToken);

    public ProfileBalanceState Read(string profileId) => _balances.Read(profileId);

    public Task<BalanceCheckOutcome> CheckAsync(string profileId,
        CancellationToken cancellationToken = default)
        => _balances.CheckAsync(profileId, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _claims.Dispose();
        _balances.Dispose();
    }
}
