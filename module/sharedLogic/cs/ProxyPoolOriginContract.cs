using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CitadelBridge;

/// <summary>
/// Read-only provenance sidecar for a proxy pool. The producer records where
/// an endpoint came from without copying credentials or provider API keys into
/// consumer state.
/// </summary>
public sealed record ProxyPoolOrigin(string Source, string AccountId);

public sealed record ProxyPoolOriginSnapshot(
    ImmutableDictionary<string, ProxyPoolOrigin> Entries,
    long Revision)
{
    public static ProxyPoolOriginSnapshot Empty { get; } = new(
        ImmutableDictionary<string, ProxyPoolOrigin>.Empty,
        0);
}

public static class ProxyPoolOriginContract
{
    private const int Schema = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string OriginsPath => Path.Combine(ProxyPoolContract.StateRoot, "proxy-origins.json");

    public static string OriginsPathFor(string poolPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolPath);
        var fullPoolPath = Path.GetFullPath(poolPath);
        return string.Equals(fullPoolPath, Path.GetFullPath(ProxyPoolContract.ActivePoolPath), StringComparison.OrdinalIgnoreCase)
            ? OriginsPath
            : Path.Combine(Path.GetDirectoryName(fullPoolPath)!, "proxy-origins.json");
    }

    public static ProxyPoolOriginSnapshot ReadSnapshot(ProxyPoolSnapshot pool, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        path ??= OriginsPath;
        if (!File.Exists(path)) return ProxyPoolOriginSnapshot.Empty;
        try
        {
            var document = JsonSerializer.Deserialize<OriginDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.Schema != Schema
                || !string.Equals(document.PoolFingerprint, Fingerprint(pool.Endpoints), StringComparison.Ordinal))
            {
                return ProxyPoolOriginSnapshot.Empty;
            }

            var active = pool.Endpoints.Select(ProxyPoolHealthContract.EndpointKey).ToHashSet(StringComparer.Ordinal);
            var entries = (document.Entries ?? [])
                .Where(entry => active.Contains(entry.EndpointKey)
                    && string.Equals(entry.Source, "webshare", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(entry.AccountId))
                .GroupBy(entry => entry.EndpointKey, StringComparer.Ordinal)
                .ToImmutableDictionary(
                    group => group.Key,
                    group => new ProxyPoolOrigin(group.First().Source, group.First().AccountId),
                    StringComparer.Ordinal);
            return new ProxyPoolOriginSnapshot(entries, new FileInfo(path).LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return ProxyPoolOriginSnapshot.Empty;
        }
    }

    public static string Serialize(IEnumerable<(ProxyEndpoint Endpoint, ProxyPoolOrigin Origin)> origins,
        IEnumerable<ProxyEndpoint> activeEndpoints)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(activeEndpoints);
        var active = activeEndpoints.ToArray();
        var activeKeys = active.Select(ProxyPoolHealthContract.EndpointKey).ToHashSet(StringComparer.Ordinal);
        var entries = origins
            .Select(item => new OriginEntry(
                ProxyPoolHealthContract.EndpointKey(item.Endpoint),
                item.Origin.Source,
                item.Origin.AccountId))
            .Where(item => activeKeys.Contains(item.EndpointKey)
                && string.Equals(item.Source, "webshare", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.AccountId))
            .GroupBy(item => item.EndpointKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.EndpointKey, StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.Serialize(new OriginDocument(Schema, Fingerprint(active), entries), JsonOptions);
    }

    public static string Fingerprint(IEnumerable<ProxyEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var canonical = endpoints.Select(item => item.Canonical)
            .OrderBy(item => item, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", canonical))));
    }

    private sealed record OriginDocument(int Schema, string PoolFingerprint, OriginEntry[]? Entries);
    private sealed record OriginEntry(string EndpointKey, string Source, string AccountId);
}
