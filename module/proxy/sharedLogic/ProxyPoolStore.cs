using System.IO;
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
    }

    public string StateRoot { get; }

    public string ActivePath { get; }

    public string BannedPath { get; }

    public string HealthPath { get; }

    public ProxyPoolSnapshot LoadActive() => ProxyPoolContract.ReadSnapshot(ActivePath);

    public ProxyPoolSnapshot LoadBanned() => ProxyPoolContract.ReadSnapshot(BannedPath);

    public ProxyHealthSnapshot LoadHealth() => ProxyPoolHealthContract.ReadSnapshot(HealthPath);

    public void Commit(IEnumerable<ProxyEndpoint> endpoints, IEnumerable<ProxyHealthRecord>? health = null)
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
        AtomicTextFile.Write(ActivePath, normalized.Select(item => item.Canonical));
        if (health is not null)
        {
            SaveHealth(health, normalized);
        }
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

        var active = LoadActive().Endpoints
            .Where(item => !selected.Contains(item.Canonical))
            .Select(item => item.Canonical)
            .ToArray();
        AtomicTextFile.Write(ActivePath, active);
        var retained = LoadHealth().Entries.Values
            .Where(record => active.Contains(record.EndpointKey, StringComparer.Ordinal));
        AtomicTextFile.WriteAllText(HealthPath, ProxyPoolHealthContract.Serialize(retained));
    }

    public void RemoveFromActive(IEnumerable<ProxyEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var selected = endpoints.Select(item => item.Canonical).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return;

        var active = LoadActive().Endpoints
            .Where(item => !selected.Contains(item.Canonical))
            .ToArray();
        AtomicTextFile.Write(ActivePath, active.Select(item => item.Canonical));
        var activeKeys = active.Select(ProxyPoolHealthContract.EndpointKey).ToHashSet(StringComparer.Ordinal);
        var retained = LoadHealth().Entries.Values.Where(record => activeKeys.Contains(record.EndpointKey));
        AtomicTextFile.WriteAllText(HealthPath, ProxyPoolHealthContract.Serialize(retained));
    }
}
