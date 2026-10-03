namespace Module.Agentrouter.Features.Claim;

/// <summary>Public boundary consumed by the Shortcuts UI.</summary>
public interface IAgentRouterClaimService : IDisposable
{
    Task<ClaimOutcome> ClaimAsync(string profileId, bool headless, string? proxy,
        CancellationToken cancellationToken = default);

    ProfileBalanceState Read(string profileId);

    Task<BalanceCheckOutcome> CheckAsync(string profileId,
        CancellationToken cancellationToken = default);

    Task<LoginCheckOutcome> CheckLoginAsync(string profileId,
        CancellationToken cancellationToken = default);
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
