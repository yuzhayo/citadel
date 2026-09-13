using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.ThunderScans;

internal static partial class ThunderScansHtmlParser
{
    private static readonly HtmlParser Parser = new();

    public static RemoteTitleDetail ParseTitle(string html, RemoteTitleIdentity identity)
    {
        var document = Parser.ParseDocument(html);
        var display = document.QuerySelector(".post-title, .manga-title-badges h1, .tab-summary h1, h1")?.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(display)) display = document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")?.Trim();
        if (string.IsNullOrWhiteSpace(display)) display = identity.Slug;
        var cover = document.QuerySelectorAll("img")
            .Select(image => image.GetAttribute("data-src") ?? image.GetAttribute("src"))
            .FirstOrDefault(url => url?.Contains("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase) == true);
        var description = document.QuerySelector(".summary__content, .description-summary, .post-content, [class*='summary']")?.TextContent.Trim();
        return new RemoteTitleDetail(new RemoteTitleSummary(identity, display, cover, null),
            string.IsNullOrWhiteSpace(description) ? null : description, [], []);
    }

    public static IReadOnlyList<RemoteChapterSummary> ParseChapters(string html, RemoteTitleIdentity title, RemoteGroupIdentity group)
    {
        var document = Parser.ParseDocument(html);
        var rows = new List<(string Id, string Number)>();
        foreach (var href in document.QuerySelectorAll("a[href]").Select(node => node.GetAttribute("href")))
        {
            if (TryChapter(href, out var id, out var number) && rows.All(row => row.Id != id)) rows.Add((id, number));
        }
        return rows.OrderBy(row => NumericSort(row.Number)).ThenBy(row => row.Number, StringComparer.OrdinalIgnoreCase)
            .Select((row, index) => new RemoteChapterSummary(
                new RemoteChapterIdentity(ThunderScansContract.SourceId, title, row.Id, row.Number, group),
                "Chapter " + row.Number, index)).ToArray();
    }

    public static IReadOnlyList<RemoteChapterSummary> ParseSitemapChapters(IEnumerable<string> urls, RemoteTitleIdentity title, RemoteGroupIdentity group)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var rows = new List<(string Id, string Number)>();
        foreach (var url in urls)
        {
            if (TryChapter(url, out var id, out var number) && rows.All(row => row.Id != id)) rows.Add((id, number));
        }
        return rows.OrderBy(row => NumericSort(row.Number)).ThenBy(row => row.Number, StringComparer.OrdinalIgnoreCase)
            .Select((row, index) => new RemoteChapterSummary(
                new RemoteChapterIdentity(ThunderScansContract.SourceId, title, row.Id, row.Number, group),
                "Chapter " + row.Number, index)).ToArray();
    }

    public static IReadOnlyList<string> ParsePages(string html)
    {
        var match = ReaderPayload().Match(html);
        if (!match.Success) throw new ThunderScansContractException("ThunderScans chapter has no ts_reader image payload.");
        try
        {
            using var document = JsonDocument.Parse(match.Groups["json"].Value);
            var sources = document.RootElement.GetProperty("sources");
            if (sources.GetArrayLength() == 0) throw new ThunderScansContractException("ThunderScans chapter image payload has no source.");
            var images = sources[0].GetProperty("images").EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (images.Length == 0) throw new ThunderScansContractException("ThunderScans chapter image payload is empty.");
            return images;
        }
        catch (JsonException exception)
        {
            throw new ThunderScansContractException("ThunderScans ts_reader payload is invalid JSON: " + exception.Message);
        }
        catch (KeyNotFoundException)
        {
            throw new ThunderScansContractException("ThunderScans ts_reader payload has an unexpected shape.");
        }
    }

    public static IReadOnlyList<string> ParseSitemapLocations(string xml) =>
        SitemapLocation().Matches(xml).Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value).Trim())
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out _)).Distinct(StringComparer.Ordinal).ToArray();

    public static string? TitleSlugFromChapterUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals(ThunderScansContract.Host, StringComparison.OrdinalIgnoreCase)) return null;
        var slug = ChapterSlug().Match(uri.AbsolutePath).Groups["slug"].Value;
        return slug.Length == 0 ? null : Uri.UnescapeDataString(slug);
    }

    public static string? TitleSlugFromChapterId(string chapterId)
    {
        if (string.IsNullOrWhiteSpace(chapterId)) return null;
        var match = ChapterSlug().Match("/" + chapterId.Trim('/') + "/");
        return match.Success ? Uri.UnescapeDataString(match.Groups["slug"].Value) : null;
    }

    public static string? TitleSlugFromTitleUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals(ThunderScansContract.Host, StringComparison.OrdinalIgnoreCase)) return null;
        var match = TitlePage().Match(uri.AbsolutePath);
        return match.Success ? Uri.UnescapeDataString(match.Groups["slug"].Value) : null;
    }

    private static bool TryChapter(string? href, out string id, out string number)
    {
        id = number = string.Empty;
        if (string.IsNullOrWhiteSpace(href)) return false;
        var path = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : href;
        var match = ChapterSlug().Match(path);
        if (!match.Success) return false;
        number = Uri.UnescapeDataString(match.Groups["number"].Value);
        id = Uri.UnescapeDataString(path.Trim('/'));
        return number.Length > 0;
    }

    private static decimal NumericSort(string text) => decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : decimal.MaxValue;

    [GeneratedRegex("ts_reader\\.run\\((?<json>\\{.*?\\})\\);", RegexOptions.Singleline)]
    private static partial Regex ReaderPayload();
    [GeneratedRegex("<loc>\\s*(?<url>.*?)\\s*</loc>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SitemapLocation();
    [GeneratedRegex("/(?<slug>.+)-chapter-(?<number>[^/?#]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterSlug();
    [GeneratedRegex("^/comics/(?<slug>[^/?#]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePage();
}
