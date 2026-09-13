using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.ThunderScans;

/// <summary>
/// Owns the complete reader-route index from the chapter sitemap. It receives
/// the reader route discovered by <see cref="ThunderScansTitleAdapter"/>.
/// </summary>
internal sealed class ThunderScansChapterAdapter(
    Func<string, CancellationToken, Task<string>> getText,
    Func<CancellationToken, Task<IReadOnlyList<string>>> getSitemapLocations)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _urlsByReaderRoute;

    public async Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
        string readerRoute,
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        CancellationToken token)
    {
        var routes = await GetUrlsByReaderRouteAsync(token).ConfigureAwait(false);
        return !routes.TryGetValue(readerRoute, out var urls)
            ? []
            : ThunderScansHtmlParser.ParseSitemapChapters(urls, title, group);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetUrlsByReaderRouteAsync(CancellationToken token)
    {
        if (_urlsByReaderRoute is not null) return _urlsByReaderRoute;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_urlsByReaderRoute is not null) return _urlsByReaderRoute;
            var sitemapUrls = await getSitemapLocations(token).ConfigureAwait(false);
            var chapterSitemaps = sitemapUrls.Where(url => url.Contains("wp-sitemap-posts-post-", StringComparison.OrdinalIgnoreCase));
            var byRoute = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sitemap in chapterSitemaps)
            foreach (var chapterUrl in ThunderScansHtmlParser.ParseSitemapLocations(await getText(sitemap, token).ConfigureAwait(false)))
            {
                if (ThunderScansHtmlParser.TitleSlugFromChapterUrl(chapterUrl) is not { } readerRoute) continue;
                if (!byRoute.TryGetValue(readerRoute, out var urls)) byRoute[readerRoute] = urls = [];
                urls.Add(chapterUrl);
            }
            return _urlsByReaderRoute = byRoute.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally { _gate.Release(); }
    }
}
