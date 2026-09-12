using CitadelBridge;

namespace Module.Mangareader.ShareLogic;

internal sealed record ProxyUsage(string EndpointKey, string Owner);

/// <summary>One lane's atomic, FIFO reservations. Credentials never enter snapshots.</summary>
internal sealed class ProxyLeaseRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProxyReservation> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Waiter> _waiting = [];

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
        string owner, IReadOnlyList<ProxyEndpoint> candidates, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (candidates.Count == 0) throw new ProxyPoolException("PROXY_POOL_EMPTY", "No compatible proxy candidates.");
        var waiter = new Waiter(owner, candidates);
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
            var endpoint = waiter.Candidates.FirstOrDefault(item => !_busy.ContainsKey(Key(item)));
            if (endpoint is null) { i++; continue; }
            var reservation = new ProxyReservation(waiter.Owner, new ProxyLease(endpoint), Release);
            _busy.Add(Key(endpoint), reservation);
            _waiting.RemoveAt(i);
            waiter.Completion.TrySetResult(reservation);
        }
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

    private sealed record Waiter(string Owner, IReadOnlyList<ProxyEndpoint> Candidates)
    {
        public TaskCompletionSource<ProxyReservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class ProxyReservation(string owner, ProxyLease lease, Action<ProxyReservation> release) : IDisposable
{
    private int _disposed;
    public string Owner { get; } = owner;
    public ProxyLease Lease { get; } = lease;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) release(this);
    }
}
