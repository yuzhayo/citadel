using Module.Mangareader.Features.Downloader.Sources.AsuraScans;
using Module.Mangareader.Features.Downloader.Sources.ThunderScans;
using Module.Mangareader.Sources;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Module.Mangareader.Downloader.Tests;

public sealed class AsuraThunderSourceTests
{
    [Fact]
    public async Task QueuedAsuraIdentityUsesPersistedTitleIdWhenSlugIsEmpty()
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("https://asurascans.com/comics/nano-machine/chapter/chapter-1", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "https://cdn.asurascans.com/asura-images/chapters/nano-machine/1/001.webp?v=42",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var source = new AsuraScansSource(client);
        var title = new RemoteTitleIdentity(AsuraScansContract.SourceId, "nano-machine", "6087", Slug: string.Empty);
        var chapter = new RemoteChapterIdentity(
            AsuraScansContract.SourceId,
            title,
            "chapter-1",
            "1",
            AsuraScansSource.Group.Identity);

        var manifest = await source.GetManifestAsync(chapter, CancellationToken.None);

        Assert.Single(manifest.Pages);
    }

    [Fact]
    public void AsuraJsonHtmlAndManifestContractsPreserveSlugAndVersionedWebpUrls()
    {
        const string catalog = """
            {"data":[{"id":6087,"slug":"nano-machine","title":"Nano Machine",
            "cover_url":"https://cdn.asurascans.com/asura-images/covers/nano-machine.e31bdb.webp",
            "chapter_count":340}],"meta":{"total":345,"per_page":20,"has_more":true}}
            """;
        const string detail = """
            {"series":{"id":6087,"slug":"nano-machine","title":"Nano Machine",
            "description":"A machine story.","cover":"https://cdn.asurascans.com/asura-images/covers/nano-machine.e31bdb.webp",
            "type":"manhwa","status":"ongoing","genres":[{"name":"Action","slug":"action"}]}}
            """;
        const string titleHtml = """
            <a href="/comics/nano-machine/chapter/chapter-297">Chapter 297</a>
            <a href="/comics/nano-machine/chapter/e910ccd1-584f-44be-be28-92661f288af0">Special</a>
            """;
        const string chapterSitemap = """
            <urlset><url><loc>https://asurascans.com/comics/nano-machine/chapter/chapter-297</loc></url>
            <url><loc>https://asurascans.com/comics/nano-machine/chapter/e910ccd1-584f-44be-be28-92661f288af0</loc></url></urlset>
            """;
        const string chapterHtml = """
            https://cdn.asurascans.com/asura-images/chapters/nano-machine/297/001.webp?v=1770499638&amp;
            https://cdn.asurascans.com/asura-images/chapters/nano-machine/297/002.webp?v=1770499638
            """;

        var page = AsuraScansJsonParser.ParseCatalog(catalog, 1);
        var title = Assert.Single(page.Items);
        var detailResult = AsuraScansJsonParser.ParseTitle(detail, title.Identity);
        var chapters = AsuraScansHtmlParser.ParseChapters(titleHtml, title.Identity, AsuraScansSource.Group.Identity);
        var sitemapChapters = AsuraScansHtmlParser.ParseSitemapChapters(chapterSitemap, title.Identity, AsuraScansSource.Group.Identity);
        var pages = AsuraScansHtmlParser.ParsePages(chapterHtml);

        Assert.Equal("nano-machine", title.Identity.Slug);
        Assert.Equal("Chapter 340", title.LatestChapterLabel);
        Assert.Equal(345, page.Total);
        Assert.True(page.HasMore);
        Assert.Equal("A machine story.", detailResult.Description);
        Assert.Equal(["Action"], detailResult.Genres.Select(genre => genre.DisplayName));
        Assert.Equal(["297", "e910ccd1-584f-44be-be28-92661f288af0"], chapters.Select(chapter => chapter.Identity.ChapterNumber));
        Assert.Equal(chapters.Select(chapter => chapter.Identity.ChapterId), sitemapChapters.Select(chapter => chapter.Identity.ChapterId));
        Assert.Equal(2, pages.Count);
        Assert.All(pages, url => Assert.Contains("?v=1770499638", url, StringComparison.Ordinal));
        Assert.Equal(20, AsuraScansContract.PageSize);
    }

    [Fact]
    public void ThunderHtmlPayloadAndSitemapIndexProduceIndependentTitleAndJpegManifestInputs()
    {
        const string titleHtml = """
            <h1>Global Martial Arts</h1><img src="https://en-thunderscans.com/wp-content/uploads/2026/07/cover.jpg">
            <a href="/global-martial-arts-chapter-371/">Chapter 371</a>
            <a href="/global-martial-arts-chapter-372/">Chapter 372</a>
            """;
        const string chapterHtml = """
            <script>ts_reader.run({"sources":[{"source":"Server 1","images":[
            "https://en-thunderscans.com/wp-content/uploads/manga/a/0001_x.jpg",
            "https://en-thunderscans.com/wp-content/uploads/manga/a/0002_y.jpg"]}]});</script>
            """;
        const string sitemap = """
            <urlset><url><loc>https://en-thunderscans.com/global-martial-arts-chapter-371/</loc></url>
            <url><loc>https://en-thunderscans.com/rebirth-of-the-ultimate-war-god-chapter-39/</loc></url></urlset>
            """;
        var identity = new RemoteTitleIdentity(ThunderScansContract.SourceId, "global-martial-arts", "global-martial-arts", "global-martial-arts");

        var detail = ThunderScansHtmlParser.ParseTitle(titleHtml, identity);
        var chapters = ThunderScansHtmlParser.ParseChapters(titleHtml, identity, ThunderScansSource.Group.Identity);
        var pages = ThunderScansHtmlParser.ParsePages(chapterHtml);
        var locations = ThunderScansHtmlParser.ParseSitemapLocations(sitemap);

        Assert.Equal("Global Martial Arts", detail.Summary.DisplayName);
        Assert.Equal(["371", "372"], chapters.Select(chapter => chapter.Identity.ChapterNumber));
        Assert.Equal(2, pages.Count);
        Assert.Equal("global-martial-arts", ThunderScansHtmlParser.TitleSlugFromChapterUrl(locations[0]));
        Assert.Equal("rebirth-of-the-ultimate-war-god", ThunderScansHtmlParser.TitleSlugFromChapterUrl(locations[1]));
        Assert.Equal("global-martial-arts", ThunderScansHtmlParser.TitleSlugFromTitleUrl("https://en-thunderscans.com/comics/global-martial-arts/"));
    }

    [Fact]
    public async Task ThunderChaptersUseTheFullSitemapInsteadOfOnlyTheVisibleTitlePageRows()
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/comics/0086250808-i-got-the-weakest-class-dragon-tamer/" => Html("<a href=\"/i-got-the-weakest-class-dragon-tamer-chapter-24/\">Chapter 24</a>"),
            "/sitemap_index.xml" => Xml("<sitemapindex><sitemap><loc>https://en-thunderscans.com/wp-sitemap-posts-post-1.xml</loc></sitemap></sitemapindex>"),
            "/wp-sitemap-posts-post-1.xml" => Xml("<urlset><url><loc>https://en-thunderscans.com/i-got-the-weakest-class-dragon-tamer-chapter-1/</loc></url><url><loc>https://en-thunderscans.com/i-got-the-weakest-class-dragon-tamer-chapter-24/</loc></url></urlset>"),
            "/i-got-the-weakest-class-dragon-tamer-chapter-1/" => Html("<script>ts_reader.run({\"sources\":[{\"images\":[\"https://en-thunderscans.com/wp-content/uploads/manga/a/0001_x.jpg\"]}]});</script>"),
            _ => throw new Xunit.Sdk.XunitException("Unexpected request: " + request.RequestUri)
        }));
        var source = new ThunderScansSource(client);
        var title = new RemoteTitleIdentity(ThunderScansContract.SourceId, "0086250808-i-got-the-weakest-class-dragon-tamer", "0086250808-i-got-the-weakest-class-dragon-tamer", "0086250808-i-got-the-weakest-class-dragon-tamer");

        var chapters = await source.GetChaptersAsync(title, ThunderScansSource.Group.Identity, CancellationToken.None);
        var manifest = await source.GetManifestAsync(chapters[0].Identity, CancellationToken.None);

        Assert.Equal(["1", "24"], chapters.Select(chapter => chapter.Identity.ChapterNumber));
        Assert.Equal("i-got-the-weakest-class-dragon-tamer-chapter-1", chapters[0].Identity.ChapterId);
        Assert.Single(manifest.Pages);
    }

    private static HttpResponseMessage Xml(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/xml")
    };

    private static HttpResponseMessage Html(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/html")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
