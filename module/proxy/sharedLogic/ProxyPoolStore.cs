using System.IO;
using System.Collections.Immutable;
using CitadelBridge;

namespace Module.Proxy.SharedLogic;

internal sealed class ProxyPoolStore
{
    private readonly object _gate = new();

    public ProxyPoolStore(string? stateRoot = null)
    {
        StateRoot = Path.GetFullPath(stateRoot ?? ProxyPoolContract.StateRoot);
        ActivePath = Path.Combine(StateRoot, "proxy.txt");
        BannedPath = Path.Combine(StateRoot, "banned-proxies.txt");
        HealthPath = ProxyPoolHealthContract.HealthPathFor(ActivePath);
        OriginsPath = ProxyPoolOriginContract.OriginsPathFor(ActivePath);
        SyncPath = Path.Combine(StateRoot, "sync-proxies.txt");
        WebsharePath = Path.Combine(StateRoot, "webshare-proxies.txt");
    }

    public string StateRoot { get; }

    public string ActivePath { get; }

    public string BannedPath { get; }

    public string HealthPath { get; }

    public string OriginsPath { get; }

    public string SyncPath { get; }

    public string WebsharePath { get; }

    public ProxyPoolSnapshot LoadActive() => ProxyPoolContract.ReadSnapshot(ActivePath);

    public ProxyPoolSnapshot LoadBanned() => ProxyPoolContract.ReadSnapshot(BannedPath);

    public ProxyHealthSnapshot LoadHealth() => ProxyPoolHealthContract.ReadSnapshot(HealthPath);

    public ProxyPoolOriginSnapshot LoadOrigins() => ProxyPoolOriginContract.ReadSnapshot(LoadActive(), OriginsPath);

    public void CommitSync(
        IEnumerable<ProxyEndpoint> endpoints,
        IEnumerable<ProxyHealthRecord>? health = null) =>
        CommitSource(endpoints, health, null, isWebshare: false);

    public void CommitSyncPartial(
        IEnumerable<ProxyEndpoint> endpoints,
        IEnumerable<ProxyHealthRecord>? health = null) =>
        CommitSource(endpoints, health, null, isWebshare: false, appendSync: true);

    public void CommitWebshare(
        IEnumerable<ProxyEndpoint> endpoints,
        IEnumerable<ProxyHealthRecord>? health = null,
        IEnumerable<(ProxyEndpoint Endpoint, ProxyPoolOrigin Origin)>? origins = null) =>
        CommitSource(endpoints, health, origins, isWebshare: true);

    private void CommitSource(
        IEnumerable<ProxyEndpoint> endpoints,
        IEnumerable<ProxyHealthRecord>? health,
        IEnumerable<(ProxyEndpoint Endpoint, ProxyPoolOrigin Origin)>? origins,
        bool isWebshare,
        bool appendSync = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var incoming = endpoints
            .GroupBy(item => item.Canonical, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Canonical, StringComparer.Ordinal)
            .ToArray();
        if (incoming.Length == 0)
        {
            throw new InvalidOperationException("No usable proxies; existing pool was preserved.");
        }

        lock (_gate)
        {
            // Legacy proxy.txt contained only the last writer's result. On the
            // first source-aware commit, classify its Webshare entries by the
            // existing provenance sidecar and preserve the rest as Sync.
            var (previousSync, previousWebshare) = LoadContributions();
            var banned = LoadBanned().Endpoints.Select(item => item.Canonical)
                .ToHashSet(StringComparer.Ordinal);
            var publishableIncoming = incoming.Where(item => !banned.Contains(item.Canonical)).ToArray();
            if (publishableIncoming.Length == 0)
            {
                throw new InvalidOperationException("No usable proxies; existing pool was preserved.");
            }
            var sync = (isWebshare ? previousSync
                    : appendSync ? previousSync.Concat(publishableIncoming) : publishableIncoming)
                .Where(item => !banned.Contains(item.Canonical)).ToArray();
            sync = sync.DistinctBy(item => item.Canonical, StringComparer.Ordinal)
                .OrderBy(item => item.Canonical, StringComparer.Ordinal).ToArray();
            var webshare = (isWebshare ? publishableIncoming : previousWebshare)
                .Where(item => !banned.Contains(item.Canonical)).ToArray();
            var combined = sync.Concat(webshare)
                .GroupBy(item => item.Canonical, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Canonical, StringComparer.Ordinal)
                .ToArray();
            if (combined.Length == 0)
            {
                throw new InvalidOperationException("No usable proxies; existing pool was preserved.");
            }

            var previousOrigins = LoadOrigins().Entries;
            IReadOnlyDictionary<string, ProxyPoolOrigin> webshareOrigins = isWebshare
                ? (origins ?? []).GroupBy(
                    item => ProxyPoolHealthContract.EndpointKey(item.Endpoint),
                    StringComparer.Ordinal).ToDictionary(
                    group => group.Key,
                    group => group.First().Origin,
                    StringComparer.Ordinal)
                : previousOrigins;
            var retainedOrigins = webshare
                .Select(endpoint =>
                {
                    var key = ProxyPoolHealthContract.EndpointKey(endpoint);
                    return webshareOrigins.TryGetValue(key, out var origin)
                        ? ((ProxyEndpoint Endpoint, ProxyPoolOrigin Origin)?)(endpoint, origin)
                        : null;
                })
                .Where(item => item.HasValue)
                .Select(item => item!.Value);

            var activeKeys = combined.Select(ProxyPoolHealthContract.EndpointKey)
                .ToHashSet(StringComparer.Ordinal);
            var newHealth = (health ?? []).Where(record => activeKeys.Contains(record.EndpointKey)).ToArray();
            var newHealthKeys = newHealth.Select(record => record.EndpointKey).ToHashSet(StringComparer.Ordinal);
            var mergedHealth = LoadHealth().Entries.Values
                .Where(record => activeKeys.Contains(record.EndpointKey)
                    && !newHealthKeys.Contains(record.EndpointKey))
                .Concat(newHealth);

            // The two source snapshots are the durable inputs. Publish the
            // derived active pointer last so readers never see half a union.
            AtomicTextFile.Write(SyncPath, sync.Select(item => item.Canonical));
            AtomicTextFile.Write(WebsharePath, webshare.Select(item => item.Canonical));
            AtomicTextFile.WriteAllText(HealthPath, ProxyPoolHealthContract.Serialize(mergedHealth));
            AtomicTextFile.WriteAllText(
                OriginsPath,
                ProxyPoolOriginContract.Serialize(retainedOrigins, combined));
            AtomicTextFile.Write(ActivePath, combined.Select(item => item.Canonical));
        }
    }

    private (ProxyEndpoint[] Sync, ProxyEndpoint[] Webshare) LoadContributions()
    {
        if (File.Exists(SyncPath) && File.Exists(WebsharePath))
        {
            return (
                ProxyPoolContract.ReadSnapshot(SyncPath).Endpoints.ToArray(),
                ProxyPoolContract.ReadSnapshot(WebsharePath).Endpoints.ToArray());
        }

        var active = LoadActive().Endpoints;
        var webshareKeys = LoadOrigins().Entries.Keys.ToHashSet(StringComparer.Ordinal);
        return (
            active.Where(item => !webshareKeys.Contains(ProxyPoolHealthContract.EndpointKey(item))).ToArray(),
            active.Where(item => webshareKeys.Contains(ProxyPoolHealthContract.EndpointKey(item))).ToArray());
    }

    public void SaveHealth(IEnumerable<ProxyHealthRecord> records, IEnumerable<ProxyEndpoint>? activeEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            var active = LoadActive().Endpoints
                .Select(ProxyPoolHealthContract.EndpointKey)
                .ToHashSet(StringComparer.Ordinal);
            var checkedKeys = (activeEndpoints ?? LoadActive().Endpoints)
                .Select(ProxyPoolHealthContract.EndpointKey)
                .ToHashSet(StringComparer.Ordinal);
            var updated = records.Where(record => active.Contains(record.EndpointKey)).ToArray();
            var retained = LoadHealth().Entries.Values
                .Where(record => active.Contains(record.EndpointKey)
                    && !checkedKeys.Contains(record.EndpointKey));
            AtomicTextFile.WriteAllText(HealthPath, ProxyPoolHealthContract.Serialize(retained.Concat(updated)));
        }
    }

    public void Ban(IEnumerable<ProxyEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var selected = endpoints.Select(item => item.Canonical).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var banned = LoadBanned().Endpoints.Select(item => item.Canonical)
                .Concat(selected)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            AtomicTextFile.Write(BannedPath, banned);

            var before = LoadActive().Endpoints;
            var origins = ProxyPoolOriginContract.ReadSnapshot(
                new ProxyPoolSnapshot(before.ToImmutableArray(), 0, 0), OriginsPath).Entries;
            var active = before
                .Where(item => !selected.Contains(item.Canonical))
                .ToArray();
            var activeKeys = active.Select(ProxyPoolHealthContract.EndpointKey).ToHashSet(StringComparer.Ordinal);
            var retained = LoadHealth().Entries.Values
                .Where(record => activeKeys.Contains(record.EndpointKey));
            AtomicTextFile.WriteAllText(HealthPath, ProxyPoolHealthContract.Serialize(retained));
            SaveOrigins(active, origins);
            RemoveFromContributions(selected);
            AtomicTextFile.Write(ActivePath, active.Select(item => item.Canonical));
        }
    }

    public void RemoveFromActive(IEnumerable<ProxyEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var selected = endpoints.Select(item => item.Canonical).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return;

        lock (_gate)
        {
            var before = LoadActive().Endpoints;
            var origins = ProxyPoolOriginContract.ReadSnapshot(
                new ProxyPoolSnapshot(before.ToImmutableArray(), 0, 0), OriginsPath).Entries;
            var active = before
                .Where(item => !selected.Contains(item.Canonical))
                .ToArray();
            var activeKeys = active.Select(ProxyPoolHealthContract.EndpointKey).ToHashSet(StringComparer.Ordinal);
            var retained = LoadHealth().Entries.Values.Where(record => activeKeys.Contains(record.EndpointKey));
            AtomicTextFile.WriteAllText(HealthPath, ProxyPoolHealthContract.Serialize(retained));
            SaveOrigins(active, origins);
            RemoveFromContributions(selected);
            AtomicTextFile.Write(ActivePath, active.Select(item => item.Canonical));
        }
    }

    private void RemoveFromContributions(IReadOnlySet<string> selected)
    {
        if (!File.Exists(SyncPath) || !File.Exists(WebsharePath)) return;
        AtomicTextFile.Write(SyncPath, ProxyPoolContract.ReadSnapshot(SyncPath).Endpoints
            .Where(item => !selected.Contains(item.Canonical)).Select(item => item.Canonical));
        AtomicTextFile.Write(WebsharePath, ProxyPoolContract.ReadSnapshot(WebsharePath).Endpoints
            .Where(item => !selected.Contains(item.Canonical)).Select(item => item.Canonical));
    }

    private void SaveOrigins(IEnumerable<ProxyEndpoint> activeEndpoints,
        IReadOnlyDictionary<string, ProxyPoolOrigin> known)
    {
        var active = activeEndpoints.ToArray();
        var retained = active
            .Select(endpoint =>
            {
                var key = ProxyPoolHealthContract.EndpointKey(endpoint);
                return known.TryGetValue(key, out var origin)
                    ? ((ProxyEndpoint Endpoint, ProxyPoolOrigin Origin)?)(endpoint, origin)
                    : null;
            })
            .Where(item => item.HasValue)
            .Select(item => item!.Value);
        AtomicTextFile.WriteAllText(OriginsPath, ProxyPoolOriginContract.Serialize(retained, active));
    }
}
