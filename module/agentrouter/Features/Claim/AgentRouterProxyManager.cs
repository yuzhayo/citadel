using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CitadelBridge;

namespace Module.Agentrouter.Features.Claim;

/// <summary>
/// Agent Router's read-only lease layer over Proxy's committed pool. It reserves
/// up to five unique HTTP(S)/SOCKS endpoints per check and keeps completed endpoints
/// in separate success/failure holds until the current pool cycle is consumed.
/// </summary>
internal sealed class AgentRouterProxyManager
{
    private const int DefaultLeaseSize = 5;
    private readonly object _sync = new();
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "agentrouter", "proxy-manager.json");
    private readonly HashSet<string> _inUse = new(StringComparer.Ordinal);
    private readonly HashSet<string> _successHold = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedHold = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checkSlots = new(4, 4);
    private TaskCompletionSource _changed = NewSignal();

    public AgentRouterProxyManager() => LoadHolds();

    public async Task<IDisposable> EnterCheckAsync(CancellationToken cancellationToken)
    {
        await _checkSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CheckSlot(_checkSlots);
    }

    public async Task<ProxyLease> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitForRelease;
            lock (_sync)
            {
                var endpoints = ProxyPoolContract.ReadSnapshot().Endpoints
                    .Where(endpoint => endpoint.Scheme is "http" or "https" or "socks4" or "socks5")
                    .ToArray();
                var current = endpoints.Select(KeyFor)
                    .ToHashSet(StringComparer.Ordinal);
                _successHold.IntersectWith(current);
                _failedHold.IntersectWith(current);

                var available = Shuffle(endpoints.Where(endpoint =>
                        !_inUse.Contains(KeyFor(endpoint))
                        && !_successHold.Contains(KeyFor(endpoint))
                        && !_failedHold.Contains(KeyFor(endpoint))))
                    .ToArray();

                if (available.Length == 0 && _inUse.Count == 0 && endpoints.Length > 0)
                {
                    // Every endpoint in this pool cycle has been tried. Start a
                    // fresh cycle, while preserving the previous cycle on disk
                    // only until this point.
                    _successHold.Clear();
                    _failedHold.Clear();
                    PersistHolds();
                    available = Shuffle(endpoints).ToArray();
                }

                if (available.Length > 0)
                {
                    var leased = available.Take(DefaultLeaseSize).ToArray();
                    foreach (var endpoint in leased)
                    {
                        _inUse.Add(KeyFor(endpoint));
                    }
                    return new ProxyLease(this, leased);
                }

                if (_inUse.Count == 0)
                {
                    return new ProxyLease(this, []);
                }

                waitForRelease = _changed.Task;
            }

            await waitForRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<ProxyEndpoint> Shuffle(IEnumerable<ProxyEndpoint> source)
        => source.OrderBy(_ => Random.Shared.Next());

    private static string KeyFor(ProxyEndpoint endpoint)
        => ProxyPoolHealthContract.EndpointKey(endpoint);

    private void Complete(ProxyLease lease, ProxyEndpoint endpoint, bool success)
    {
        lock (_sync)
        {
            var key = KeyFor(endpoint);
            if (!lease.MarkCompleted(key))
            {
                return;
            }

            if (success)
            {
                _successHold.Add(key);
                _failedHold.Remove(key);
            }
            else
            {
                _failedHold.Add(key);
                _successHold.Remove(key);
            }
            PersistHolds();
        }
    }

    private void Release(ProxyLease lease)
    {
        lock (_sync)
        {
            foreach (var endpoint in lease.Endpoints)
            {
                _inUse.Remove(KeyFor(endpoint));
            }

            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
    }

    private void LoadHolds()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonNode.Parse(File.ReadAllText(_statePath)) as JsonObject;
            ReadSet(state?["success_hold"] as JsonArray, _successHold);
            ReadSet(state?["failed_hold"] as JsonArray, _failedHold);
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void PersistHolds()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var state = new JsonObject
            {
                ["success_hold"] = new JsonArray(_successHold
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["failed_hold"] = new JsonArray(_failedHold
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            };
            var staging = _statePath + ".tmp";
            File.WriteAllText(staging, state.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            File.Move(staging, _statePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ReadSet(JsonArray? values, HashSet<string> target)
    {
        if (values is null) return;
        foreach (var value in values)
        {
            if (value is JsonValue item && item.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                target.Add(text);
            }
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CheckSlot(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) slots.Release();
        }
    }

    internal sealed class ProxyLease : IDisposable
    {
        private readonly AgentRouterProxyManager _owner;
        private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
        private int _disposed;

        internal ProxyLease(AgentRouterProxyManager owner, ProxyEndpoint[] endpoints)
        {
            _owner = owner;
            Endpoints = endpoints;
        }

        public IReadOnlyList<ProxyEndpoint> Endpoints { get; }

        public void MarkSuccess(ProxyEndpoint endpoint)
            => _owner.Complete(this, endpoint, success: true);

        public void MarkFailure(ProxyEndpoint endpoint)
            => _owner.Complete(this, endpoint, success: false);

        internal bool MarkCompleted(string canonical)
            => Endpoints.Any(endpoint => KeyFor(endpoint) == canonical)
                && _completed.Add(canonical);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(this);
            }
        }
    }
}
