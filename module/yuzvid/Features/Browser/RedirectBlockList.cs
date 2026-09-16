using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// Document-navigation blocklist with a maintained upstream.
///
/// Hand-curated host blocking does not scale (a new redirector appears every
/// week), so the working set is EasyList's `||host^` rules, fetched weekly and
/// cached on disk. The baked fallback below exists ONLY for first-run/offline
/// and contains hosts proven by local trace evidence — never guesses.
/// </summary>
internal static class RedirectBlockList
{
    // Baked fallback. tsyndicate/marzaent: popup-trace.log 2026-09-16,
    // main tab walked igay69 → tsyndicate → marzaent → stripchat twice.
    private static readonly string[] BakedHosts =
    [
        "tsyndicate.com",
        "go.marzaent.com",
    ];

    private const string ListUrl = "https://easylist.to/easylist/easylist.txt";
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);

    private static readonly object Gate = new();
    private static HashSet<string>? _hosts;
    private static HashSet<string>? _exceptions;
    private static bool _refreshStarted;

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "Yuzvid", "easylist-hosts.txt");

    /// <summary>True when the URI's host (or any parent domain) is listed.</summary>
    public static bool IsBlockedUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        return IsBlockedHost(parsed.Host);
    }

    public static bool IsBlockedHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        EnsureLoaded();
        if (MatchesAny(_exceptions, host)) return false;
        return MatchesAny(_hosts, host);
    }

    private static bool MatchesAny(HashSet<string>? set, string host)
    {
        if (set is null) return false;
        if (set.Contains(host)) return true;
        foreach (var rule in set)
        {
            if (host.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_hosts is not null) return;
            var merged = new HashSet<string>(BakedHosts, StringComparer.OrdinalIgnoreCase);
            var mergedAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(CachePath))
                {
                    foreach (var line in File.ReadLines(CachePath))
                    {
                        var trimmed = line.Trim().ToLowerInvariant();
                        if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                        if (trimmed.StartsWith("@@"))
                        {
                            var host = trimmed[2..];
                            if (host.Length > 0) mergedAllowed.Add(host);
                        }
                        else merged.Add(trimmed);
                    }
                }
            }
            catch { /* corrupt cache — baked list still applies */ }
            _hosts = merged;
            _exceptions = mergedAllowed;

            var stale = true;
            try { stale = !File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > MaxCacheAge; }
            catch { }
            if (stale && !_refreshStarted)
            {
                _refreshStarted = true;
                _ = Task.Run(RefreshAsync);
            }
        }
    }

    private static async Task RefreshAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var text = await http.GetStringAsync(ListUrl).ConfigureAwait(false);
            var parsed = new HashSet<string>(BakedHosts, StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ParseEasyList(text, parsed, allowed);
            if (parsed.Count == 0) return;

            var dir = Path.GetDirectoryName(CachePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            var lines = new List<string>(parsed);
            lines.AddRange(allowed.Select(h => "@@" + h));
            await File.WriteAllLinesAsync(CachePath, lines).ConfigureAwait(false);

            lock (Gate) { _hosts = parsed; _exceptions = allowed; }
            PopupTrace.Write("easylist", $"refreshed, {parsed.Count} hosts");
        }
        catch (Exception ex)
        {
            PopupTrace.Write("easylist", "refresh failed (offline?), keeping baked list: " + ex.Message);
        }
    }

    /// <summary>
    /// Minimal ABP subset for DOCUMENT decisions only:
    /// - block: `||host^` with no options (generic) or with a `document` option;
    /// - allow: `@@||host^` under the same condition (checked first).
    /// Rules scoped to other types ($script/$image/…) are ignored — they must
    /// not cancel whole-page navigations. Full ABP semantics (domain=,
    /// third-party, …) is out of scope; mismatches fail open (allow).
    /// </summary>
    internal static void ParseEasyList(
        string text, HashSet<string> blocked, HashSet<string> allowed)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            bool isException = line.StartsWith("@@", StringComparison.Ordinal);
            if (isException) line = line[2..];
            if (!line.StartsWith("||", StringComparison.Ordinal)) continue;

            var rest = line[2..];
            string hostPart = rest;
            string options = string.Empty;
            var dollar = rest.IndexOf('$');
            if (dollar >= 0)
            {
                hostPart = rest[..dollar];
                options = rest[(dollar + 1)..];
            }
            var end = hostPart.IndexOfAny(new[] { '^', '/', '|' });
            var host = (end < 0 ? hostPart : hostPart[..end]).Trim().ToLowerInvariant();
            if (host.Length == 0 || host.Contains('*') || host.Contains(' ') || !host.Contains('.'))
                continue;

            if (options.Length > 0 && !HasOption(options, "document"))
                continue; // scoped to other resource types — not a document rule

            (isException ? allowed : blocked).Add(host);
        }
    }

    private static bool HasOption(string options, string name)
    {
        foreach (var opt in options.Split(','))
        {
            var o = opt.Trim();
            if (o.Equals(name, StringComparison.OrdinalIgnoreCase)
                || o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
