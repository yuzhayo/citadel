using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.ManualUrl;

internal sealed record DynamicManualTitlePage(
    RemoteTitleDetail Detail,
    IReadOnlyList<RemoteChapterSummary> Chapters);

/// <summary>
/// Parses public, server-rendered manga pages by document signature. It owns no
/// hostname and does not alter or call any provider-specific parser.
/// </summary>
internal static partial class DynamicManualHtmlParser
{
    private const string ChapterLinkSelector =
        ".comics-table-wrap a[href], #chapter-list a[href], .chapter-list a[href], "
        + "li.wp-manga-chapter a[href], .entry-content table a[href]";

    public static DynamicManualTitlePage? TryParseTitle(Uri pageUrl, string html)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument(html);
        var name = Text(document.QuerySelector(
            "h1.entry-title, div.post-title h1, #manga-title > h1, main h1"));
        if (name is null) return null;

        var identity = TitleIdentity(pageUrl);
        var chapters = ParseChapters(document, identity);
        var isMadaraTitle = document.QuerySelector(
            "#manga-chapters-holder, input.rating-post-id, div.summary_image") is not null;
        if (chapters.Count == 0 && !isMadaraTitle) return null;

        var cover = document.QuerySelectorAll(
                "div.summary_image img, .entry-content figure img, .entry-content img")
            .Where(image => !image.ClassList.Contains("wp-manga-chapter-img")
                && !image.ClassList.Contains("chapter-img"))
            .Select(image => ImageUrl(image, pageUrl))
            .FirstOrDefault(url => url is not null);

        var description = document.QuerySelectorAll(
                "div.description-summary div.summary__content, div.summary_content div.manga-excerpt, "
                + ".entry-content p, article p")
            .Select(Text)
            .Where(text => text is { Length: >= 20 }
                && !text.Contains("You are Reading", StringComparison.OrdinalIgnoreCase)
                && ChapterWordCount(text) < 3)
            .OrderByDescending(text => text!.Length)
            .FirstOrDefault();

        var genres = document.QuerySelectorAll(
                "div.genres-content a, div.tags-content a, a[rel~=tag]")
            .Select(element => Text(element))
            .Where(text => text is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(text => new RemoteOption(Slug(text!), text!))
            .ToArray();

        var latest = chapters
            .Select(chapter => ParseDecimal(chapter.Identity.ChapterNumber))
            .Where(number => number is not null)
            .Select(number => number!.Value)
            .DefaultIfEmpty()
            .Max();
        var detail = new RemoteTitleDetail(
            new RemoteTitleSummary(
                identity,
                name,
                cover,
                latest > 0 ? "Ch. " + NumberText(latest) : null),
            description,
            genres,
            [new RemoteOption("Source", pageUrl.Host)]);
        return new DynamicManualTitlePage(detail, chapters);
    }

    public static IReadOnlyList<RemoteChapterSummary> ParseChapters(
        string html,
        Uri titleUrl,
        RemoteTitleIdentity title)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(titleUrl);
        ArgumentNullException.ThrowIfNull(title);
        return ParseChapters(new HtmlParser().ParseDocument(html), title);
    }

    public static IReadOnlyList<string> ParsePages(Uri chapterUrl, string html)
    {
        ArgumentNullException.ThrowIfNull(chapterUrl);
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument(html);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = new List<string>();
        foreach (var image in document.QuerySelectorAll(
                     ".chapter-content img, .reading-content img, img.wp-manga-chapter-img"))
        {
            var url = ImageUrl(image, chapterUrl);
            if (url is not null && seen.Add(url)) pages.Add(url);
        }

        if (pages.Count == 0)
        {
            throw new DynamicManualContractException(
                "Halaman chapter tidak memiliki gambar manga yang dapat dibaca.");
        }

        return pages;
    }

    private static IReadOnlyList<RemoteChapterSummary> ParseChapters(
        IDocument document,
        RemoteTitleIdentity title)
    {
        var group = DynamicManualSource.Group.Identity;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chapters = new List<(decimal Number, string NumberText, string Name, string Url)>();
        foreach (var link in document.QuerySelectorAll(ChapterLinkSelector))
        {
            var name = Text(link);
            var href = link.GetAttribute("href");
            if (name is null
                || !TryAbsoluteHttpUrl(href, new Uri(title.TitleId), out var url)
                || !TryChapterNumber(name + " " + url.AbsolutePath, out var number)
                || !seen.Add(url.AbsoluteUri))
            {
                continue;
            }

            chapters.Add((number, NumberText(number), name, url.AbsoluteUri));
        }

        return chapters
            .OrderBy(chapter => chapter.Number)
            .ThenBy(chapter => chapter.Url, StringComparer.Ordinal)
            .Select((chapter, index) => new RemoteChapterSummary(
                new RemoteChapterIdentity(
                    DynamicManualSource.SourceId,
                    title,
                    chapter.Url,
                    chapter.NumberText,
                    group),
                chapter.Name,
                index))
            .ToArray();
    }

    private static RemoteTitleIdentity TitleIdentity(Uri pageUrl)
    {
        var builder = new UriBuilder(pageUrl) { Fragment = string.Empty };
        var canonical = builder.Uri.AbsoluteUri;
        return new RemoteTitleIdentity(
            DynamicManualSource.SourceId,
            canonical,
            canonical,
            canonical);
    }

    private static string? ImageUrl(IElement image, Uri baseUrl)
    {
        foreach (var attribute in new[] { "data-src", "data-lazy-src", "data-cfsrc", "data-manga-src", "src" })
        {
            if (TryAbsoluteHttpUrl(image.GetAttribute(attribute), baseUrl, out var url))
            {
                return url.AbsoluteUri;
            }
        }

        foreach (var attribute in new[] { "data-srcset", "srcset" })
        {
            var sourceSet = image.GetAttribute(attribute);
            if (string.IsNullOrWhiteSpace(sourceSet)) continue;
            foreach (var candidate in sourceSet.Split(',').Reverse())
            {
                var value = candidate.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (TryAbsoluteHttpUrl(value, baseUrl, out var url)) return url.AbsoluteUri;
            }
        }

        return null;
    }

    private static bool TryAbsoluteHttpUrl(string? value, Uri baseUrl, out Uri url)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(baseUrl, value.Trim(), out var candidate)
            && candidate.Scheme is "https" or "http")
        {
            url = candidate;
            return true;
        }

        url = null!;
        return false;
    }

    private static bool TryChapterNumber(string value, out decimal number)
    {
        number = 0;
        var match = ChapterNumberRegex().Match(value);
        return match.Success
            && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    private static decimal? ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string NumberText(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static int ChapterWordCount(string value) =>
        Regex.Matches(value, "chapter", RegexOptions.IgnoreCase).Count;

    private static string Slug(string value) =>
        SlugRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');

    private static string? Text(IElement? element)
    {
        var text = element?.TextContent;
        if (string.IsNullOrWhiteSpace(text)) return null;
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"(?:chapter|ch)[\s\-_:#]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterNumberRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
