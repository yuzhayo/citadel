using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.ThunderScans;

/// <summary>
/// Owns Thunder's title route: catalog entries point at /comics/{title-route}/.
/// That route can differ from the reader route embedded in its chapter links.
/// </summary>
internal sealed class ThunderScansTitleAdapter(
    Func<string, CancellationToken, Task<string>> getText,
    Func<CancellationToken, Task<IReadOnlyList<string>>> getSitemapLocations)
{
    private IReadOnlyList<string>? _titleRoutes;

    public async Task<IReadOnlyList<string>> GetTitleRoutesAsync(CancellationToken token)
    {
        if (_titleRoutes is not null) return _titleRoutes;
        var sitemapUrls = await getSitemapLocations(token).ConfigureAwait(false);
        var titleSitemaps = sitemapUrls.Where(url => url.Contains("wp-sitemap-posts-manga-", StringComparison.OrdinalIgnoreCase));
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sitemap in titleSitemaps)
        foreach (var titleUrl in ThunderScansHtmlParser.ParseSitemapLocations(await getText(sitemap, token).ConfigureAwait(false)))
            if (ThunderScansHtmlParser.TitleSlugFromTitleUrl(titleUrl) is { } route) routes.Add(route);
        return _titleRoutes = routes.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<RemoteTitleDetail> GetDetailAsync(RemoteTitleIdentity title, CancellationToken token) =>
        ThunderScansHtmlParser.ParseTitle(await getText(TitlePath(title), token).ConfigureAwait(false), title);

    public async Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        CancellationToken token)
    {
        return ThunderScansHtmlParser.ParseChapters(
            await getText(TitlePath(title), token).ConfigureAwait(false), title, group);
    }

    private static string TitlePath(RemoteTitleIdentity title) =>
        ThunderScansContract.BaseUrl + "/comics/" + Uri.EscapeDataString(RouteSlug(title)) + "/";

    private static string RouteSlug(RemoteTitleIdentity title) =>
        !string.IsNullOrWhiteSpace(title.Slug) ? title.Slug : title.TitleId;
}
