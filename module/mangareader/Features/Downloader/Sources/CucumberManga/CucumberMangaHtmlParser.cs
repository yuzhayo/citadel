using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.CucumberManga;

/// <summary>
/// Pure parsing boundary for Cucumber Manga HTML. Network code hands untrusted
/// documents in; only validated provider identities and same-site HTTPS URLs
/// leave this class.
/// </summary>
internal static partial class CucumberMangaHtmlParser
{
    private const string ArchiveSelector =
        "div.page-item-detail, .manga__item, .c-tabs-item__content";

    public static RemoteCatalogPage ParseBrowse(string html, int page)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));

        var document = Parse(html);
        var results = new List<RemoteTitleSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in document.QuerySelectorAll(ArchiveSelector))
        {
            var id = Attribute(card, "data-post-id")
                ?? card.QuerySelector("[data-post-id]")?.GetAttribute("data-post-id")?.Trim();
            var link = card.QuerySelector(".post-title a");
            var name = Text(link);
            var href = link?.GetAttribute("href")?.Trim();
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(name)
                || !TryTitleSlug(href, out var slug)
                || !seen.Add(id))
            {
                continue;
            }

            var cover = card.QuerySelector("img") is { } image ? ImageUrl(image) : null;
            var latest = Text(card.QuerySelector(".list-chapter .chapter-item a, .list-chapter a"));
            results.Add(new RemoteTitleSummary(
                new RemoteTitleIdentity(
                    CucumberMangaContract.SourceId,
                    id,
                    slug,
                    slug),
                name,
                cover,
                latest));
        }

        return new RemoteCatalogPage(
            results,
            Total: null,
            page,
            HasMore: results.Count == CucumberMangaContract.PageSize);
    }

    public static RemoteTitleDetail ParseTitleDetail(string html, RemoteTitleIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(identity);
        var document = Parse(html);

        var reportedId = Attribute(document.QuerySelector("#manga-chapters-holder"), "data-id")
            ?? Attribute(document.QuerySelector("input.rating-post-id"), "value")
            ?? ShortlinkId(document);
        if (reportedId is not null
            && !string.Equals(reportedId, identity.TitleId, StringComparison.Ordinal))
        {
            throw new CucumberMangaContractException(
                $"Cucumber Manga detail identity changed from '{identity.TitleId}' to '{reportedId}'.");
        }

        var name = Text(document.QuerySelector("div.post-title h1, #manga-title > h1"))
            ?? throw Missing("detail title");
        var cover = document.QuerySelector("div.summary_image img") is { } image
            ? ImageUrl(image)
            : null;
        var description = Description(document);

        var genres = document
            .QuerySelectorAll("div.genres-content a, div.tags-content a")
            .Select(option => new RemoteOption(OptionKey(option), Text(option) ?? string.Empty))
            .Where(option => option.DisplayName.Length > 0)
            .DistinctBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var metadata = new List<RemoteOption>();
        foreach (var item in document.QuerySelectorAll("div.post-content_item"))
        {
            var label = Text(item.QuerySelector(".summary-heading h5"))?.TrimEnd(':');
            var value = Text(item.QuerySelector(".summary-content"));
            if (string.IsNullOrWhiteSpace(label)
                || string.IsNullOrWhiteSpace(value)
                || label.Equals("Tag(s)", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Genre(s)", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            metadata.Add(new RemoteOption(label, value));
        }

        return new RemoteTitleDetail(
            new RemoteTitleSummary(identity, name, cover, LatestChapterLabel: null),
            description,
            genres,
            metadata);
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
        foreach (var item in document.QuerySelectorAll("li.wp-manga-chapter"))
        {
            var link = item.QuerySelector("a");
            var href = link?.GetAttribute("href")?.Trim();
            var name = Text(link);
            if (name is null
                || !TryChapterSlug(href, title.TitleHid, out var chapterId)
                || !seen.Add(chapterId))
            {
                continue;
            }

            var number = ChapterNumber(name);
            chapters.Add(new RemoteChapterSummary(
                new RemoteChapterIdentity(
                    CucumberMangaContract.SourceId,
                    title,
                    chapterId,
                    number,
                    group),
                name,
                chapters.Count));
        }

        return chapters;
    }

    public static IReadOnlyList<string> ParsePages(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = Parse(html);
        var pages = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var images = document.QuerySelectorAll(
            "div.reading-content div.page-break img, "
            + "div.reading-content li.blocks-gallery-item img, "
            + "div.reading-content img.wp-manga-chapter-img");
        foreach (var image in images)
        {
            var url = ImageUrl(image);
            if (url is not null && seen.Add(url)) pages.Add(url);
        }

        if (pages.Count == 0)
        {
            throw new CucumberMangaContractException(
                "Cucumber Manga chapter tidak berisi URL gambar yang valid.");
        }

        return pages;
    }

    private static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    private static string? Description(IDocument document)
    {
        var container = document.QuerySelector(
            "div.description-summary div.summary__content, div.summary_content div.manga-excerpt");
        if (container is null) return null;

        var paragraphs = container.QuerySelectorAll("p")
            .Select(Text)
            .Where(text => text is not null)
            .Cast<string>()
            .ToArray();
        return paragraphs.Length > 0 ? string.Join("\n\n", paragraphs) : Text(container);
    }

    private static string OptionKey(IElement element)
    {
        var href = element.GetAttribute("href");
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) return segments[^1];
        }

        return SlugRegex().Replace((Text(element) ?? string.Empty).ToLowerInvariant(), "-").Trim('-');
    }

    private static string? ImageUrl(IElement image)
    {
        foreach (var attribute in new[] { "data-srcset", "srcset" })
        {
            if (LargestSrcSet(image.GetAttribute(attribute)) is { } candidate
                && IsProviderHttpsUrl(candidate))
            {
                return candidate;
            }
        }

        foreach (var attribute in new[] { "data-src", "data-lazy-src", "data-cfsrc", "data-manga-src", "src" })
        {
            var candidate = image.GetAttribute(attribute)?.Trim();
            if (IsProviderHttpsUrl(candidate)) return candidate;
        }

        return null;
    }

    private static string? LargestSrcSet(string? sourceSet)
    {
        if (string.IsNullOrWhiteSpace(sourceSet)) return null;

        string? best = null;
        double bestWidth = double.MinValue;
        foreach (var raw in sourceSet.Split(','))
        {
            var pieces = WhitespaceRegex().Split(raw.Trim());
            if (pieces.Length == 0 || !IsProviderHttpsUrl(pieces[0])) continue;
            var width = pieces.Length > 1
                && double.TryParse(
                    pieces[1].TrimEnd('w', 'x'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 0;
            if (best is null || width > bestWidth)
            {
                best = pieces[0];
                bestWidth = width;
            }
        }

        return best;
    }

    private static bool TryTitleSlug(string? href, out string slug)
    {
        slug = string.Empty;
        if (!TryProviderUri(href, out var uri)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2
            || !segments[0].Equals("manga", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        slug = Uri.UnescapeDataString(segments[1]);
        return slug.Length > 0;
    }

    private static bool TryChapterSlug(string? href, string titleSlug, out string chapterSlug)
    {
        chapterSlug = string.Empty;
        if (!TryProviderUri(href, out var uri)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3
            || !segments[0].Equals("manga", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals(titleSlug, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        chapterSlug = Uri.UnescapeDataString(segments[2]);
        return chapterSlug.Length > 0;
    }

    private static bool IsProviderHttpsUrl(string? value) =>
        TryProviderUri(value, out _);

    private static bool TryProviderUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            && candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && (candidate.Host.Equals(CucumberMangaContract.Host, StringComparison.OrdinalIgnoreCase)
                || candidate.Host.EndsWith("." + CucumberMangaContract.Host, StringComparison.OrdinalIgnoreCase)))
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

    private static string? ShortlinkId(IDocument document)
    {
        var href = document.QuerySelector("link[rel=shortlink]")?.GetAttribute("href");
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "p") return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }

    private static string? Attribute(IElement? element, string name)
    {
        var value = element?.GetAttribute(name)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? Text(IElement? element)
    {
        if (element is null) return null;
        var value = WhitespaceRegex().Replace(element.TextContent, " ").Trim();
        return value.Length == 0 ? null : value;
    }

    private static CucumberMangaContractException Missing(string field) =>
        new($"Cucumber Manga response is missing '{field}'; provider HTML changed.");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex ChapterNumberRegex();
}
