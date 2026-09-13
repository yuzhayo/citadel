using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.AsuraScans;

internal static partial class AsuraScansHtmlParser
{
    private static readonly HtmlParser Parser = new();

    public static RemoteTitleDetail ParseTitle(string html, RemoteTitleIdentity identity)
    {
        var document = Parser.ParseDocument(html);
        var title = document.QuerySelector("h1")?.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = identity.Slug;
        var cover = document.QuerySelectorAll("img")
            .Select(image => image.GetAttribute("src") ?? image.GetAttribute("data-src"))
            .FirstOrDefault(url => url?.Contains("cdn.asurascans.com", StringComparison.OrdinalIgnoreCase) == true);
        var description = document.QuerySelector(".description, .synopsis, [class*='description']")?.TextContent.Trim();
        return new RemoteTitleDetail(
            new RemoteTitleSummary(identity, title, cover, null)
            {
                CoverRequestHeaders = AsuraScansJsonParser.RefererHeaders(),
            },
            string.IsNullOrWhiteSpace(description) ? null : description,
            [], []);
    }

    public static IReadOnlyList<RemoteChapterSummary> ParseChapters(
        string html, RemoteTitleIdentity title, RemoteGroupIdentity group)
    {
        var document = Parser.ParseDocument(html);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RemoteChapterSummary>();
        foreach (var href in document.QuerySelectorAll("a[href]").Select(node => node.GetAttribute("href")))
        {
            if (!TryChapter(href, title.Slug, out var id, out var number) || !seen.Add(id)) continue;
            result.Add(new RemoteChapterSummary(
                new RemoteChapterIdentity(AsuraScansContract.SourceId, title, id, number, group),
                "Chapter " + number,
                result.Count));
        }
        return result;
    }

    /// <summary>
    /// The documented fallback for Astro title pages that do not render their
    /// chapter links in the initial HTML. The caller caches the sitemap payload;
    /// this parser only maps the requested title's canonical chapter URLs.
    /// </summary>
    public static IReadOnlyList<RemoteChapterSummary> ParseSitemapChapters(
        string sitemapXml, RemoteTitleIdentity title, RemoteGroupIdentity group)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RemoteChapterSummary>();
        foreach (var match in SitemapLocation().Matches(sitemapXml).Cast<Match>())
        {
            if (!TryChapter(WebUtility.HtmlDecode(match.Groups["url"].Value), title.Slug, out var id, out var number)
                || !seen.Add(id)) continue;
            result.Add(new RemoteChapterSummary(
                new RemoteChapterIdentity(AsuraScansContract.SourceId, title, id, number, group),
                "Chapter " + number, result.Count));
        }
        return result;
    }

    public static IReadOnlyList<string> ParsePages(string html)
    {
        var decoded = WebUtility.HtmlDecode(html);
        var urls = CdnUrl().Matches(decoded).Select(match => match.Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) throw new AsuraScansContractException("AsuraScans chapter has no CDN page URLs.");
        return urls;
    }

    private static bool TryChapter(string? href, string slug, out string id, out string number)
    {
        id = number = string.Empty;
        if (string.IsNullOrWhiteSpace(href)) return false;
        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
        var match = ChapterPath().Match(path);
        if (!match.Success || !string.Equals(match.Groups["slug"].Value, slug, StringComparison.OrdinalIgnoreCase)) return false;
        id = Uri.UnescapeDataString(match.Groups["chapter"].Value);
        number = id.StartsWith("chapter-", StringComparison.OrdinalIgnoreCase) ? id[8..] : id;
        return !string.IsNullOrWhiteSpace(number);
    }

    [GeneratedRegex("https://cdn\\.asurascans\\.com/asura-images/chapters/[^\"\\s<]+?\\.webp(?:\\?v=\\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex CdnUrl();

    [GeneratedRegex("/comics/(?<slug>[^/]+)/chapter/(?<chapter>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPath();

    [GeneratedRegex("<loc>\\s*(?<url>.*?)\\s*</loc>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SitemapLocation();
}
