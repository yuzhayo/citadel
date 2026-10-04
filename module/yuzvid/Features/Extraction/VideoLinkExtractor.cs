using System;
using System.Collections.Generic;
using System.Net;
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
// Added 2026-10-04 (NOT from urllib_lib): VersionGroups (igay69 linksv1..4
//   server-player versions), anchor-label parsing (igay69 anchor text), and
//   display-title derivation. Evidence: ARTEFACT/igay69-probe/log.md §servers,
//   ARTEFACT/IMPLEMENTATION_PLAN-ige69.md (linksv3 = playmogo/myvidplay links).

/// <summary>One video-host URL found on a page, with ported metadata.</summary>
public sealed record VideoLink(
    string Full,
    string Host,
    string Path,
    string Slug,
    int? EpisodeNum,
    string? EpisodeTitle,
    string ServerGroup,
    bool IsDirectMedia,
    string VersionGroup = "",
    string? Label = null,
    string? Title = null)
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

    /// <summary>Row title for the drawer; falls back to the slug when none derived.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Slug : Title!;

    /// <summary>Secondary row line: "host · slug".</summary>
    public string DetailLine => Host + " \u00b7 " + Slug;
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

    // igay69 server-player versions (linksv1..linksv4 tab panels). Host→version.
    // Evidence: igay69-probe/log.md "Server 1 DoodStream / Server 2 VOE /
    // Server 3 PlayMogo / Server 4 Byseqekaho"; IMPLEMENTATION_PLAN-ige69.md
    // (linksv3 = playmogo/myvidplay). Only V3 (playmogo) is downloadable today.
    private static readonly Dictionary<string, string> VersionGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["luluvid.com"] = "V1", ["luluvdoo.com"] = "V1", ["luluvdo.com"] = "V1",
        ["dood.wf"] = "V1", ["doodstream.co"] = "V1",
        ["voe.sx"] = "V2", ["voe.pm"] = "V2", ["voe.unblockit.top"] = "V2",
        ["playmogo.com"] = "V3", ["myvidplay.com"] = "V3",
        ["byseqekaho.com"] = "V4",
    };

    // Human label for a version group (header text in the drawer).
    public static string VersionLabel(string version) => version switch
    {
        "V1" => "V1 \u00b7 DoodStream",
        "V2" => "V2 \u00b7 VOE",
        "V3" => "V3 \u00b7 PlayMogo",
        "V4" => "V4 \u00b7 Byseqekaho",
        _ => version,
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

    // igay69 labeled anchors: <a href="…">link | video | streaming & download | part 01</a>.
    // Text cannot contain nested tags (plain-text anchors, per IMPLEMENTATION_PLAN-ige69.md).
    private static readonly Regex AnchorPattern = new(
        @"<a[^>]+href\s*=\s*[""'](?<url>https?://[^""']+)[""'][^>]*>(?<text>[^<]{1,200})</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// igay69 anchor labels: URL → anchor text. Gives hash-slug links a readable
    /// title ("link | video | streaming &amp; download | part 01"). Best-effort:
    /// pages without labeled anchors simply yield no entries.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ExtractAnchorLabels(string html)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return labels;

        foreach (Match m in AnchorPattern.Matches(html))
        {
            var url = WebUtility.HtmlDecode(m.Groups["url"].Value).Trim();
            var text = WebUtility.HtmlDecode(m.Groups["text"].Value).Trim();
            if (url.Length == 0 || text.Length == 0) continue;
            labels[url] = text;
        }
        return labels;
    }

    /// <summary>
    /// Display title, best-first: URL episode title (byseqekaho style) →
    /// igay69 anchor label → humanized word-like slug → null (caller falls
    /// back to the slug for hash-only pages).
    /// </summary>
    public static string? DeriveTitle(string? label, string? episodeTitle, string slug, int? episodeNum)
    {
        if (!string.IsNullOrWhiteSpace(episodeTitle))
            return episodeNum is { } n ? $"{episodeTitle} Part {n:00}" : episodeTitle;

        if (!string.IsNullOrWhiteSpace(label))
        {
            var seg = label.Trim();
            var lastSep = seg.LastIndexOf('|');
            if (lastSep >= 0 && lastSep + 1 < seg.Length)
                seg = seg[(lastSep + 1)..].Trim();
            if (seg.Length > 0
                && !seg.Equals("download", StringComparison.OrdinalIgnoreCase)
                && !seg.Equals("watch", StringComparison.OrdinalIgnoreCase))
                return Humanize(seg);
        }

        if (!IsHashLike(slug))
            return Humanize(slug);

        return null;
    }

    /// <summary>Hash-only slug (e.g. snu73d1s27wb) carries no words to humanize.</summary>
    private static bool IsHashLike(string slug)
        => slug.Length >= 8 && !slug.Contains('-') && !slug.Contains('_');

    private static string Humanize(string text)
    {
        var words = text.Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1) builder.Append(word, 1, word.Length - 1);
        }
        return builder.ToString();
    }

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

    /// <summary>igay69 server-player version (V1..V4) for a host, or "" when unknown.</summary>
    public static string GetVersionGroup(string host)
    {
        foreach (var kv in VersionGroups)
        {
            if (host.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return string.Empty;
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

        var labels = ExtractAnchorLabels(html ?? string.Empty);
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

            labels.TryGetValue(cleaned, out var label);
            var link = BuildLink(cleaned, uri, host, label);
            if (seen.Add(link.DedupKey))
                result.Add(link);
        }

        return result;
    }

    /// <summary>
    /// Build a single link from an absolute URI (Source B tap path).
    /// Null when the string is not an absolute http(s) URL with a dotted host.
    /// </summary>
    public static VideoLink? TryBuildFromUri(string full, string? label = null)
    {
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        var host = uri.Host;
        if (string.IsNullOrEmpty(host) || !host.Contains('.'))
            return null;
        return BuildLink(full, uri, host, label);
    }

    private static VideoLink BuildLink(string full, Uri uri, string host, string? label = null)
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

        // Uniform display title (label/episode/slug aware).
        var displayTitle = DeriveTitle(label, title, slug, episodeNum);

        return new VideoLink(
            full, host, path, slug, episodeNum, title,
            GetServerGroup(host), IsDirectMediaUrl(full),
            GetVersionGroup(host), label, displayTitle);
    }
}
