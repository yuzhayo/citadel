using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Module.Yuzvid.Features.Extraction;

// ─── Provenance (T3.1 freeze — DO NOT edit hosts without updating this) ─────
// Ported from: C:\VSCODE\LTX-QUASAR\dood\url_extract\urllib_lib.py
// Source SHA256: DA64F48D735224CA82132943DB2A077C04A064101A21031947AB3D27D8BFD53
// Source lines: 627. Ported 2026-09-16.
// Ported: DEFAULT_VIDEO_HOSTS, SERVER_GROUPS, URL_PATTERN, TRAILING_JUNK,
//   host-substring filter, dedup-by-path, slug/episode/title metadata.
// NOT ported (out of Phase 3 scope): ouo.io unwrap, pagination, Playwright
//   fetch, retry/backoff, per-server JSON file output.
// Deviation: host match is OrdinalIgnoreCase (Python `in` is case-sensitive;
//   real-world hosts are lowercase, so behavior is identical on real input).

/// <summary>One video-host URL found on a page, with ported metadata.</summary>
public sealed record VideoLink(
    string Full,
    string Host,
    string Path,
    string Slug,
    int? EpisodeNum,
    string? EpisodeTitle,
    string ServerGroup,
    bool IsDirectMedia)
{
    /// <summary>Dedup identity: origin + path, query ignored (urllib_lib rule).</summary>
    public string DedupKey
    {
        get
        {
            if (!Uri.TryCreate(Full, UriKind.Absolute, out var uri))
                return Full.ToLowerInvariant();
            return (uri.Host + uri.AbsolutePath).ToLowerInvariant().TrimEnd('/');
        }
    }

    /// <summary>Drawer badge text. Empty for embed pages, marked for direct media.</summary>
    public string DirectBadge => IsDirectMedia ? "\u25cf direct" : string.Empty;
}

/// <summary>
/// Static HTML → video-host links. Pure logic, no I/O, no WebView2.
/// Mirrors urllib_lib._extract_urls_from_html + _filter_video_urls +
/// _dedup_urls + _extract_metadata.
/// </summary>
public static class VideoLinkExtractor
{
    // Frozen from urllib_lib.DEFAULT_VIDEO_HOSTS (see provenance above).
    public static readonly IReadOnlyList<string> DefaultVideoHosts = new[]
    {
        // DoodStream variants
        "luluvdoo.com", "luluvid.com", "luluvdo.com", "dood.wf", "doodstream.co",
        "playmogo.com", "myvidplay.com",
        // VOE
        "voe.sx", "voe.pm", "voe.unblockit.top",
        // Byseqekaho
        "byseqekaho.com",
        // Generic video hosts
        "streamtape.com", "streamvid.net", "mixdrop.co",
        "alphaembed.cc", "embedsb.com",
    };

    // Frozen from urllib_lib.SERVER_GROUPS.
    private static readonly Dictionary<string, string> ServerGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playmogo.com"] = "playmogo",
        ["myvidplay.com"] = "playmogo",
        ["luluvid.com"] = "luluvid",
        ["luluvdoo.com"] = "luluvid",
        ["luluvdo.com"] = "luluvid",
        ["dood.wf"] = "luluvid",
        ["doodstream.co"] = "luluvid",
        ["voe.sx"] = "voe",
        ["voe.pm"] = "voe",
        ["byseqekaho.com"] = "byseqekaho",
    };

    private static readonly Regex UrlPattern =
        new(@"https?://[^\s'""<>\\]+", RegexOptions.Compiled);

    private static readonly Regex TrailingJunk =
        new(@"[,:;\)\]\}]+$", RegexOptions.Compiled);

    private static readonly Regex[] EpisodePatterns =
    {
        new(@"[_\-]part[_\-]?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"[_\-]ep(?:isode)?[_\-]?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"[_\-](\d+)(?:[_\-]|$)", RegexOptions.Compiled),
        new(@"part[_\-]?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly Regex TitleSuffix =
        new(@"[_\-]?(?:part|ep(?:isode)?)?[_\-]?\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DirectMediaExtensions =
        { ".mp4", ".m3u8", ".webm" };

    /// <summary>
    /// True when the URI is directly downloadable media (extension match).
    /// Used by the Source B tap; same rule here keeps A/B consistent.
    /// </summary>
    public static bool IsDirectMediaUrl(string uri)
    {
        var path = uri.Split('?', '#')[0];
        foreach (var ext in DirectMediaExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string GetServerGroup(string host)
    {
        foreach (var kv in ServerGroups)
        {
            if (host.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        var labels = host.Split('.');
        return labels.Length >= 2 ? labels[^2] : host;
    }

    /// <summary>
    /// Extract + filter + dedup + metadata. Mirrors the urllib_lib pipeline
    /// minus fetching (the caller supplies rendered HTML).
    /// </summary>
    public static IReadOnlyList<VideoLink> ExtractFromHtml(
        string html, IEnumerable<string>? hosts = null)
    {
        var hostList = hosts is null
            ? DefaultVideoHosts
            : new List<string>(hosts);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<VideoLink>();

        foreach (Match m in UrlPattern.Matches(html ?? string.Empty))
        {
            var cleaned = TrailingJunk.Replace(m.Value, string.Empty);
            if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
                continue;
            var host = uri.Host;
            if (string.IsNullOrEmpty(host) || !host.Contains('.'))
                continue;

            bool hostHit = false;
            foreach (var h in hostList)
            {
                if (cleaned.Contains(h, StringComparison.OrdinalIgnoreCase))
                {
                    hostHit = true;
                    break;
                }
            }
            if (!hostHit && !IsDirectMediaUrl(cleaned))
                continue;

            var link = BuildLink(cleaned, uri, host);
            if (seen.Add(link.DedupKey))
                result.Add(link);
        }

        return result;
    }

    /// <summary>
    /// Build a single link from an absolute URI (Source B tap path).
    /// Null when the string is not an absolute http(s) URL with a dotted host.
    /// </summary>
    public static VideoLink? TryBuildFromUri(string full)
    {
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        var host = uri.Host;
        if (string.IsNullOrEmpty(host) || !host.Contains('.'))
            return null;
        return BuildLink(full, uri, host);
    }

    private static VideoLink BuildLink(string full, Uri uri, string host)
    {
        var path = uri.AbsolutePath;
        var trimmed = path.TrimEnd('/');
        var slug = trimmed.Contains('/')
            ? trimmed[(trimmed.LastIndexOf('/') + 1)..]
            : trimmed;

        int? episodeNum = null;
        foreach (var pattern in EpisodePatterns)
        {
            var match = pattern.Match(path);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
            {
                episodeNum = num;
                break;
            }
        }

        // byseqekaho-style title: /d/{hash}/title-part-01
        string? title = null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[^2] == "d")
        {
            var titlePart = TitleSuffix.Replace(parts[^1], string.Empty)
                .Replace("-", " ").Replace("_", " ").Trim();
            if (titlePart.Length > 0)
                title = titlePart;
        }

        return new VideoLink(
            full, host, path, slug, episodeNum, title,
            GetServerGroup(host), IsDirectMediaUrl(full));
    }
}
