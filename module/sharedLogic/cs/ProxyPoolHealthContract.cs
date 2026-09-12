using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CitadelBridge;

/// <summary>Read-only health sidecar for the canonical proxy pool.</summary>
public enum ProxyHealthState
{
    Unknown,
    Healthy,
    Unreachable,
}

public sealed record ProxyHealthRecord(
    string EndpointKey,
    ProxyHealthState State,
    long? LatencyMilliseconds,
    DateTimeOffset CheckedAtUtc,
    string? Detail);

public sealed record ProxyHealthSnapshot(
    ImmutableDictionary<string, ProxyHealthRecord> Entries,
    long Revision)
{
    public static ProxyHealthSnapshot Empty { get; } = new(
        ImmutableDictionary<string, ProxyHealthRecord>.Empty,
        0);
}

/// <summary>
/// Owns the health sidecar location and opaque endpoint identity. The sidecar
/// never stores a canonical proxy URL, so it cannot expose credentials.
/// </summary>
public static class ProxyPoolHealthContract
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string HealthPath => Path.Combine(ProxyPoolContract.StateRoot, "proxy-health.json");

    public static string HealthPathFor(string poolPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolPath);
        var fullPoolPath = Path.GetFullPath(poolPath);
        if (string.Equals(fullPoolPath, Path.GetFullPath(ProxyPoolContract.ActivePoolPath), StringComparison.OrdinalIgnoreCase))
        {
            return HealthPath;
        }
        return Path.Combine(Path.GetDirectoryName(fullPoolPath)!, "proxy-health.json");
    }

    public static string EndpointKey(ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Canonical)));
    }

    public static ProxyHealthSnapshot ReadSnapshot(string? path = null)
    {
        path ??= HealthPath;
        if (!File.Exists(path)) return ProxyHealthSnapshot.Empty;
        try
        {
            var entries = JsonSerializer.Deserialize<ProxyHealthRecord[]>(File.ReadAllText(path), JsonOptions) ?? [];
            var normalized = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.EndpointKey))
                .GroupBy(entry => entry.EndpointKey, StringComparer.Ordinal)
                .ToImmutableDictionary(group => group.Key, group => group.OrderByDescending(item => item.CheckedAtUtc).First(), StringComparer.Ordinal);
            return new ProxyHealthSnapshot(normalized, new FileInfo(path).LastWriteTimeUtc.Ticks);
        }
        catch (JsonException)
        {
            return ProxyHealthSnapshot.Empty;
        }
        catch (IOException)
        {
            return ProxyHealthSnapshot.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ProxyHealthSnapshot.Empty;
        }
    }

    public static string Serialize(IEnumerable<ProxyHealthRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var normalized = records
            .Where(record => !string.IsNullOrWhiteSpace(record.EndpointKey))
            .GroupBy(record => record.EndpointKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.CheckedAtUtc).First())
            .OrderBy(record => record.EndpointKey, StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
