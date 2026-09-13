using CitadelBridge;

namespace Module.Mangareader.ShareLogic;

internal sealed record ProxyUsage(string EndpointKey, string Owner);

/// <summary>One lane's atomic, FIFO reservations. Credentials never enter snapshots.</summary>
internal sealed class ProxyLeaseRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProxyReservation> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Waiter> _waiting = [];
    private readonly Dictionary<string, long> _accountLastGranted = new(StringComparer.Ordinal);
    private long _grantSequence;

    /// <summary>
    /// An opaque identity for one authenticated endpoint. Server alone is not
    /// enough: pool providers can expose independent routes at the same server
    /// under different account credentials. The raw canonical value is never
    /// exposed through snapshots or UI.
    /// </summary>
    internal static string Key(ProxyEndpoint endpoint) => ProxyPoolHealthContract.EndpointKey(endpoint);

    public IReadOnlyList<ProxyUsage> Snapshot()
    {
        lock (_gate) return [.. _busy.Select(pair => new ProxyUsage(pair.Key, pair.Value.Owner))];
    }

    public async Task<ProxyReservation> ReserveAsync(
        string owner,
        IReadOnlyList<ProxyEndpoint> candidates,
        CancellationToken token,
        Func<ProxyEndpoint, string?>? accountFor = null)
    {
        token.ThrowIfCancellationRequested();
        if (candidates.Count == 0) throw new ProxyPoolException("PROXY_POOL_EMPTY", "No compatible proxy candidates.");
        var waiter = new Waiter(owner, candidates, accountFor);
        lock (_gate)
        {
            _waiting.Add(waiter);
            Grant();
        }
        try
        {
            return await waiter.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _waiting.Remove(waiter);
                if (waiter.Completion.Task.IsCompletedSuccessfully)
                    waiter.Completion.Task.Result.Dispose();
                Grant();
            }
            throw;
        }
    }

    private void Grant()
    {
        for (var i = 0; i < _waiting.Count;)
        {
            var waiter = _waiting[i];
            var endpoint = SelectEndpoint(waiter);
            if (endpoint is null) { i++; continue; }
            var accountId = waiter.AccountFor?.Invoke(endpoint);
            var reservation = new ProxyReservation(waiter.Owner, new ProxyLease(endpoint), accountId, Release);
            _busy.Add(Key(endpoint), reservation);
            if (!string.IsNullOrWhiteSpace(accountId)) _accountLastGranted[accountId] = ++_grantSequence;
            _waiting.RemoveAt(i);
            waiter.Completion.TrySetResult(reservation);
        }
    }

    private ProxyEndpoint? SelectEndpoint(Waiter waiter)
    {
        var available = waiter.Candidates.Where(item => !_busy.ContainsKey(Key(item))).ToArray();
        if (available.Length == 0) return null;
        if (waiter.AccountFor is null) return available[0];

        var managed = available
            .Select(endpoint => (Endpoint: endpoint, AccountId: waiter.AccountFor(endpoint)))
            .Where(item => !string.IsNullOrWhiteSpace(item.AccountId))
            .ToArray();
        if (managed.Length == 0) return available[0];

        var activeByAccount = _busy.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.AccountId))
            .GroupBy(item => item.AccountId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var account = managed.Select(item => item.AccountId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => activeByAccount.GetValueOrDefault(id))
            .ThenBy(id => _accountLastGranted.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .First();
        return managed.First(item => string.Equals(item.AccountId, account, StringComparison.Ordinal)).Endpoint;
    }

    private void Release(ProxyReservation reservation)
    {
        lock (_gate)
        {
            var key = Key(reservation.Lease.Endpoint);
            if (_busy.TryGetValue(key, out var current) && ReferenceEquals(current, reservation)) _busy.Remove(key);
            Grant();
        }
    }

    private sealed record Waiter(
        string Owner,
        IReadOnlyList<ProxyEndpoint> Candidates,
        Func<ProxyEndpoint, string?>? AccountFor)
    {
        public TaskCompletionSource<ProxyReservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class ProxyReservation(
    string owner,
    ProxyLease lease,
    string? accountId,
    Action<ProxyReservation> release) : IDisposable
{
    private int _disposed;
    public string Owner { get; } = owner;
    public ProxyLease Lease { get; } = lease;
    public string? AccountId { get; } = accountId;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) release(this);
    }
}
