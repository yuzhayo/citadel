using System.IO;
using System.Collections.Immutable;
using CitadelBridge;

namespace Module.Proxy.SharedLogic;

internal sealed class ProxyPoolStore
{
    public ProxyPoolStore(string? stateRoot = null)
    {
        StateRoot = Path.GetFullPath(stateRoot ?? ProxyPoolContract.StateRoot);
        ActivePath = Path.Combine(StateRoot, "proxy.txt");
        BannedPath = Path.Combine(StateRoot, "banned-proxies.txt");
        HealthPath = ProxyPoolHealthContract.HealthPathFor(ActivePath);
        OriginsPath = ProxyPoolOriginContract.OriginsPathFor(ActivePath);
    }

    public string StateRoot { get; }

    public string ActivePath { get; }

    public string BannedPath { get; }

    public string HealthPath { get; }

    public string OriginsPath { get; }

    public ProxyPoolSnapshot LoadActive() => ProxyPoolContract.ReadSnapshot(ActivePath);

    public ProxyPoolSnapshot LoadBanned() => ProxyPoolContract.ReadSnapshot(BannedPath);

    public ProxyHealthSnapshot LoadHealth() => ProxyPoolHealthContract.ReadSnapshot(HealthPath);

    public ProxyPoolOriginSnapshot LoadOrigins() => ProxyPoolOriginContract.ReadSnapshot(LoadActive(), OriginsPath);

    public void Commit(
        IEnumerable<ProxyEndpoint> endpoints,
        IEnumerable<ProxyHealthRecord>? health = null,
        IEnumerable<(ProxyEndpoint Endpoint, ProxyPoolOrigin Origin)>? origins = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var normalized = endpoints
            .GroupBy(item => item.Canonical, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Canonical, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("No usable proxies; existing pool was preserved.");
        }
        if (health is not null)
        {
            SaveHealth(health, normalized);
        }
        // A Sync commit has no Webshare provenance. Replacing the sidecar is
        // intentional: a consumer must never attribute a new pool to an old key.
        // Write dependent metadata before the active pointer. Until the final
        // pointer swap, consumers keep seeing the old pool and reject this
        // sidecar by fingerprint; after it, all three files describe one pool.
        AtomicTextFile.WriteAllText(
            OriginsPath,
            ProxyPoolOriginContract.Serialize(origins ?? [], normalized));
        AtomicTextFile.Write(ActivePath, normalized.Select(item => item.Canonical));
    }

    public void SaveHealth(IEnumerable<ProxyHealthRecord> records, IEnumerable<ProxyEndpoint>? activeEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var active = (activeEndpoints ?? LoadActive().Endpoints)
            .Select(ProxyPoolHealthContract.EndpointKey)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = records.Where(record => active.Contains(record.EndpointKey));
        AtomicTextFile.WriteAllText(HealthPath, ProxyPoolHealthContract.Serialize(filtered));
    }

    public void Ban(IEnumerable<ProxyEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var selected = endpoints.Select(item => item.Canonical).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
        {
            return;
        }

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
        AtomicTextFile.Write(ActivePath, active.Select(item => item.Canonical));
    }

    public void RemoveFromActive(IEnumerable<ProxyEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var selected = endpoints.Select(item => item.Canonical).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return;

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
        AtomicTextFile.Write(ActivePath, active.Select(item => item.Canonical));
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
