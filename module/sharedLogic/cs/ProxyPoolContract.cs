using System.Collections.Immutable;
using System.Globalization;
using System.IO;

namespace CitadelBridge;

/// <summary>
/// File contract shared by the Proxy producer and read-only citizen adapters.
/// It owns the canonical location and syntax, but no routing policy.
/// </summary>
public static class ProxyPoolContract
{
    private static readonly HashSet<string> SupportedSchemes = new(
        ["http", "https", "socks4", "socks5"],
        StringComparer.OrdinalIgnoreCase);

    public static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel",
        "Proxy");

    public static string ActivePoolPath => Path.Combine(StateRoot, "proxy.txt");

    public static string BannedPoolPath => Path.Combine(StateRoot, "banned-proxies.txt");

    public static string SettingsPath => Path.Combine(StateRoot, "settings.json");

    public static ProxyPoolSnapshot ReadSnapshot(string? path = null)
    {
        path ??= ActivePoolPath;
        if (!File.Exists(path))
        {
            return new ProxyPoolSnapshot([], 0, 0);
        }

        var info = new FileInfo(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        var parsed = ParseLines(lines);
        return parsed with { Revision = info.LastWriteTimeUtc.Ticks };
    }

    public static ProxyPoolSnapshot ParseLines(
        IEnumerable<string> lines,
        string? defaultScheme = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var endpoints = new SortedDictionary<string, ProxyEndpoint>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var line in lines)
        {
            var candidate = line?.Trim();
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith('#'))
            {
                continue;
            }

            if (!TryParse(candidate, defaultScheme, out var endpoint))
            {
                skipped++;
                continue;
            }

            endpoints.TryAdd(endpoint.Canonical, endpoint);
        }

        return new ProxyPoolSnapshot(endpoints.Values.ToImmutableArray(), 0, skipped);
    }

    public static bool TryParse(
        string? value,
        string? defaultScheme,
        out ProxyEndpoint endpoint)
    {
        endpoint = default!;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(defaultScheme))
            {
                return false;
            }
            text = defaultScheme.Trim().ToLowerInvariant() + "://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !SupportedSchemes.Contains(uri.Scheme)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port is < 1 or > 65535
            || !HasExplicitPort(text)
            || (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string? username = null;
        string? password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            password = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : null;
            if (string.IsNullOrWhiteSpace(username))
            {
                return false;
            }
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var serverHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var server = $"{scheme}://{serverHost}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        var auth = username is null
            ? string.Empty
            : Uri.EscapeDataString(username) + (password is null ? string.Empty : ":" + Uri.EscapeDataString(password)) + "@";
        var canonical = $"{scheme}://{auth}{serverHost}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        endpoint = new ProxyEndpoint(scheme, host, uri.Port, username, password, server, canonical);
        return true;
    }

    private static bool HasExplicitPort(string value)
    {
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..]
            .Split('/', '?', '#')[0];
        var hostPort = authority.Contains('@', StringComparison.Ordinal)
            ? authority[(authority.LastIndexOf('@') + 1)..]
            : authority;
        if (hostPort.StartsWith("[", StringComparison.Ordinal))
        {
            var close = hostPort.IndexOf(']');
            return close >= 0 && close + 1 < hostPort.Length && hostPort[close + 1] == ':';
        }
        return hostPort.LastIndexOf(':') > 0;
    }
}

public sealed record ProxyEndpoint(
    string Scheme,
    string Host,
    int Port,
    string? Username,
    string? Password,
    string Server,
    string Canonical)
{
    public bool HasAuthentication => Username is not null;

    public string Masked => HasAuthentication
        ? $"{Scheme}://***@{FormatHost(Host)}:{Port}"
        : Server;

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
}

public sealed record ProxyPoolSnapshot(
    ImmutableArray<ProxyEndpoint> Endpoints,
    long Revision,
    int SkippedLines);

public sealed class ProxyPoolException : Exception
{
    public ProxyPoolException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
