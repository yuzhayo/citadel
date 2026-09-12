using System.Collections.Concurrent;
using CitadelBridge;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>One chapter execution's finite candidate and fallback budgets.</summary>
internal sealed class QueueIndependentSession : IDisposable
{
    private readonly string _owner;
    private readonly string _sourceId;
    private readonly ProxyPoolAdapter _pool;
    private readonly QueueSharedSessionAdapter _shared;
    private readonly Action<string> _status;
    private readonly IReadOnlyList<ProxyEndpoint> _candidates;
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, HashSet<string>> _attempted = new();
    private readonly ConcurrentDictionary<int, PageFetchResult> _terminal = new();
    internal ProxyHttpTransport Transport { get; }
    internal string Owner => _owner;

    public QueueIndependentSession(string owner, string sourceId, ProxyPoolAdapter pool,
        QueueSharedSessionAdapter shared, Action<string> status)
    {
        _owner = owner;
        _sourceId = sourceId;
        _pool = pool;
        _shared = shared;
        _status = status;
        _candidates = pool.Candidates(ProxyTarget.Http);
        Transport = new ProxyHttpTransport(pool);
    }

    internal async Task<PageFetchResult> FetchAsync(RemotePage page,
        Func<ProxyLease, CancellationToken, Task<PageFetchResult>> native,
        Func<CancellationToken, Task<PageFetchResult>> fallback, CancellationToken token)
    {
        if (_terminal.TryGetValue(page.Ordinal, out var terminal)) return terminal;
        var attempted = _attempted.GetOrAdd(page.Ordinal, _ => new(StringComparer.OrdinalIgnoreCase));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var sharedServers = _pool.Reservations.Snapshot().Where(item => item.Owner == "downloader-browser")
                .Select(item => item.Server).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var eligible = _candidates.Where(item => !attempted.Contains(ProxyLeaseRegistry.Key(item))
                && !_failed.ContainsKey(ProxyLeaseRegistry.Key(item)) && !sharedServers.Contains(ProxyLeaseRegistry.Key(item))).ToArray();
            if (eligible.Length == 0) break;
            _status($"Independent page {page.Ordinal + 1}: waiting for proxy");
            using var reservation = await _pool.Reservations.ReserveAsync(
                _owner + "/page-" + page.Ordinal, eligible, token).ConfigureAwait(false);
            var key = ProxyLeaseRegistry.Key(reservation.Lease.Endpoint);
            // Another page may have marked it failed while this request waited.
            if (_failed.ContainsKey(key)) continue;
            attempted.Add(key);
            _status($"Independent page {page.Ordinal + 1} · {key} · attempt {attempted.Count}");
            var result = await native(reservation.Lease, token).ConfigureAwait(false);
            if (result.Succeeded) return result;
            if (result.Outcome != PageFetchOutcome.NetworkFailed)
            {
                // Missing/oversized/provider denial/cooldown are not transport
                // failures. Do not rotate proxies around a provider response.
                _terminal[page.Ordinal] = result;
                return result;
            }
            _failed.TryAdd(key, 0);
        }
        _status($"Shared fallback page {page.Ordinal + 1}");
        var borrowed = await _shared.BorrowAsync(_sourceId, true, fallback, token).ConfigureAwait(false);
        _terminal[page.Ordinal] = borrowed; // one shared attempt across recovery
        return borrowed;
    }

    public void Dispose() => Transport.Dispose();
}
