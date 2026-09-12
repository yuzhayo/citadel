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
    }

    public string StateRoot { get; }

    public string ActivePath { get; }

    public string BannedPath { get; }

    public ProxyPoolSnapshot LoadActive() => ProxyPoolContract.ReadSnapshot(ActivePath);

    public ProxyPoolSnapshot LoadBanned() => ProxyPoolContract.ReadSnapshot(BannedPath);

    public void Commit(IEnumerable<ProxyEndpoint> endpoints)
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
    }
}
