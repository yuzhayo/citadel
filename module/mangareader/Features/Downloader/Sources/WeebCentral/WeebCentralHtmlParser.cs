using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.WeebCentral;

/// <summary>
/// Pure parsing boundary for Weeb Central HTML. Network code hands untrusted
/// documents in; only validated provider identities, provider-host HTTPS routes
/// and reader image URLs leave this class.
/// </summary>
internal static partial class WeebCentralHtmlParser
{
    private const string GridCardSelector = "article.bg-base-300";
    private const string SeriesLinkSelector = "a[href*=\"/series/\"]";
    private const string TitleLinkSelector = "a.line-clamp-1";
    private const string MoreButtonSelector = "button[hx-get*=\"search/data\"]";
    private const string ChapterLinkSelector = "a[href*=\"/chapters/\"]";
    private const string ImagesRootSelector = "#chapter-images";

    public static RemoteCatalogPage ParseGrid(string html, int page)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));

        var document = Parse(html);
        var results = new List<RemoteTitleSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in document.QuerySelectorAll(GridCardSelector))
        {
            if (!TrySeriesRoute(card.QuerySelector(SeriesLinkSelector)?.GetAttribute("href"),
                    out var titleId, out var slug)
                || !seen.Add(titleId))
            {
                continue;
            }

            var name = Text(card.QuerySelector(TitleLinkSelector))
                ?? CoverTitle(card)
                ?? slug;
            results.Add(new RemoteTitleSummary(
                new RemoteTitleIdentity(WeebCentralContract.SourceId, titleId, titleId, slug),
                name,
                WeebCentralContract.CoverUrl(titleId),
                null)
            {
                CoverFallbackUrls = WeebCentralContract.CoverFallbacks(titleId),
            });
        }

        // The grid carries no count or page tokens: the trailing "View More
        // Results" button is the one authoritative signal that another batch exists.
        var hasMore = document.QuerySelector(MoreButtonSelector) is not null;
        return new RemoteCatalogPage(results, Total: null, page, hasMore);
    }

    public static RemoteTitleDetail ParseTitleDetail(string html, RemoteTitleIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(identity);

        var document = Parse(html);
        var name = Text(document.QuerySelector("h1")) ?? identity.Slug;
        var summary = new RemoteTitleSummary(
            identity,
            name,
            WeebCentralContract.CoverUrl(identity.TitleId),
            null)
        {
            CoverFallbackUrls = WeebCentralContract.CoverFallbacks(identity.TitleId),
        };
        return new RemoteTitleDetail(summary, Description(document), TagLinks(document), Metadata(document));
    }

    public static IReadOnlyList<RemoteChapterSummary> ParseChapters(
        string html,
        RemoteTitleIdentity title,
        RemoteGroupIdentity group)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(group);

        var document = Parse(html);
        var chapters = new List<RemoteChapterSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var anchor in document.QuerySelectorAll(ChapterLinkSelector))
        {
            if (!TryChapterId(anchor.GetAttribute("href"), out var chapterId) || !seen.Add(chapterId))
            {
                continue;
            }

            var display = Text(anchor) ?? chapterId;
            chapters.Add(new RemoteChapterSummary(
                new RemoteChapterIdentity(
                    WeebCentralContract.SourceId,
                    title,
                    chapterId,
                    ChapterNumber(display),
                    group),
                display,
                chapters.Count));
        }

        return chapters;
    }

    /// <summary>
    /// Ordered page URLs from one reader fragment. The long-strip response carries
    /// the whole chapter, so document order is page order.
    /// </summary>
    public static IReadOnlyList<string> ParsePages(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var document = Parse(html);
        var root = document.QuerySelector(ImagesRootSelector) ?? document.Body;
        var pages = new List<string>();
        if (root is null) return pages;

        foreach (var image in root.QuerySelectorAll("img"))
        {
            var source = image.GetAttribute("src")?.Trim();
            if (IsPageImage(source)) pages.Add(source!);
        }

        return pages;
    }

    private static IDocument Parse(string html) =>
        new HtmlParser().ParseDocument(html ?? throw new ArgumentNullException(nameof(html)));

    private static string? Description(IDocument document)
    {
        foreach (var item in document.QuerySelectorAll("li"))
        {
            var label = Text(item.QuerySelector(":scope > strong"));
            if (label is null || !label.StartsWith("Description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The description keeps its own line breaks; only the ends are trimmed.
            var paragraph = item.QuerySelector("p");
            return (paragraph?.TextContent ?? string.Empty).Trim();
        }

        return null;
    }

    private static IReadOnlyList<RemoteOption> Metadata(IDocument document)
    {
        var metadata = new List<RemoteOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.QuerySelectorAll("li"))
        {
            var strong = item.QuerySelector(":scope > strong");
            var label = Text(strong);
            if (strong is null || label is null) continue;
            label = label.TrimEnd(':').Trim();
            if (label.Length == 0
                || label.StartsWith("Description", StringComparison.OrdinalIgnoreCase)
                || label.StartsWith("Tag", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(label))
            {
                continue;
            }

            var value = ValueAfterStrong(item, strong);
            if (value is not null) metadata.Add(new RemoteOption(label, value));
        }

        return metadata;
    }

    private static IReadOnlyList<RemoteOption> TagLinks(IDocument document)
    {
        var tags = new List<RemoteOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in document.QuerySelectorAll("a[href*=\"included_tag=\"]"))
        {
            if (!Uri.TryCreate(anchor.GetAttribute("href"), UriKind.Absolute, out var uri)) continue;
            var value = QueryValue(uri.Query, "included_tag");
            if (value is null || !seen.Add(value)) continue;
            tags.Add(new RemoteOption(value, Text(anchor) ?? value));
        }

        return tags;
    }

    private static string? ValueAfterStrong(IElement item, IElement strong)
    {
        var builder = new StringBuilder();
        foreach (var node in item.ChildNodes)
        {
            if (ReferenceEquals(node, strong)) continue;
            builder.Append(node.TextContent);
        }

        var value = Clean(builder.ToString());
        return value.Length == 0 ? null : value;
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals(key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1]).Replace('+', ' ');
            }
        }

        return null;
    }

    private static string? CoverTitle(IElement card)
    {
        var alt = card.QuerySelector("img[alt]")?.GetAttribute("alt")?.Trim();
        if (string.IsNullOrWhiteSpace(alt)) return null;
        var cleaned = alt.EndsWith(" cover", StringComparison.OrdinalIgnoreCase)
            ? alt[..^6].Trim()
            : alt;
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static bool TrySeriesRoute(string? href, out string titleId, out string slug)
    {
        titleId = string.Empty;
        slug = string.Empty;
        if (!TryProviderUri(href, out var uri)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !segments[0].Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        titleId = segments[1].Trim();
        slug = segments.Length >= 3 ? Uri.UnescapeDataString(segments[2]).Trim() : titleId;
        return titleId.Length > 0 && slug.Length > 0;
    }

    private static bool TryChapterId(string? href, out string chapterId)
    {
        chapterId = string.Empty;
        // Chapter links arrive both absolute and root-relative on the same page.
        if (!Uri.TryCreate(href?.Trim(), UriKind.RelativeOrAbsolute, out var candidate)) return false;
        string path;
        if (candidate.IsAbsoluteUri)
        {
            if (!candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !IsProviderHost(candidate.Host))
            {
                return false;
            }

            path = candidate.AbsolutePath;
        }
        else
        {
            path = candidate.OriginalString.Split('?', 2)[0];
        }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2
            || !segments[0].Equals("chapters", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        chapterId = Uri.UnescapeDataString(segments[1]).Trim();
        return chapterId.Length > 0;
    }

    /// <summary>
    /// Reader images are served from a separate poster CDN, so any absolute HTTPS
    /// image is accepted; the site's own placeholder is rejected by path.
    /// </summary>
    private static bool IsPageImage(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.Contains("/static/images/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderHost(string host) =>
        host.Equals(WeebCentralContract.Host, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + WeebCentralContract.Host, StringComparison.OrdinalIgnoreCase);

    private static bool TryProviderUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            && candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && IsProviderHost(candidate.Host))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string ChapterNumber(string displayName)
    {
        var match = ChapterNumberRegex().Match(displayName);
        return match.Success ? match.Value : string.Empty;
    }

    private static string? Text(IElement? element)
    {
        if (element is null) return null;
        var value = Clean(element.TextContent);
        return value.Length == 0 ? null : value;
    }

    private static string Clean(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex ChapterNumberRegex();
}



