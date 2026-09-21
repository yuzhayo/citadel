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
        Assert.Equal(["297"], chapters.Select(chapter => chapter.Identity.ChapterNumber));
        Assert.Equal(chapters.Select(chapter => chapter.Identity.ChapterId), sitemapChapters.Select(chapter => chapter.Identity.ChapterId));
        Assert.Equal(2, pages.Count);
        Assert.All(pages, url => Assert.Contains("?v=1770499638", url, StringComparison.Ordinal));
        Assert.Equal(20, AsuraScansContract.PageSize);
    }

    [Fact]
    public void AsuraNullSearchDataIsAnEmptyResultInsteadOfAContractFailure()
    {
        var page = AsuraScansJsonParser.ParseCatalog(
            "{\"data\":null,\"meta\":{\"per_page\":28}}",
            1);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData("fun territory", "fun-territory")]
    [InlineData("I Got The Weakest Class, Dragon Tamer!?", "i-got-the-weakest-class-dragon-tamer")]
    public void ThunderSearchUsesTheSameWordSeparatorsAsItsTitleSlugs(
        string query,
        string expected)
    {
        Assert.Equal(expected, ThunderScansSource.NormalizeSearchText(query));
    }

    [Fact]
    public void AsuraDetailAndAstroChaptersMapProviderFieldsToTheirDisplayContract()
    {
        const string detail = """
            {"series":{"id":2110,"slug":"im-not-that-kind-of-talent","title":"I'm Not That Kind of Talent",
            "description":"<p>First sentence.</p><p>Second sentence.</p>","cover":"https://cdn.asurascans.com/cover.webp",
            "type":"manhwa","status":"ongoing","author":"Denfee","artist":"Meona",
            "genres":[{"name":"Action","slug":"action"}]}}
            """;
        const string titleHtml = """
            &quot;id&quot;:[0,163937],&quot;series_id&quot;:[0,2110],&quot;number&quot;:[0,89],&quot;slug&quot;:[0,&quot;7395790b-5358-41a0-a83b-68aa28e6fcd3&quot;],&quot;page_count&quot;:[0,16],&quot;is_premium&quot;:[0,false],&quot;series_slug&quot;:[0,&quot;im-not-that-kind-of-talent&quot;]
            """;
        var identity = new RemoteTitleIdentity(
            AsuraScansContract.SourceId,
            "im-not-that-kind-of-talent",
            "2110",
            "im-not-that-kind-of-talent");

        var parsedDetail = AsuraScansJsonParser.ParseTitle(detail, identity);
        var chapters = AsuraScansHtmlParser.ParseChapters(
            titleHtml,
            identity,
            AsuraScansSource.Group.Identity);

        Assert.Equal("First sentence. Second sentence.", parsedDetail.Description);
        Assert.Contains(parsedDetail.Metadata, item => item.DisplayName == "Type" && item.Key == "manhwa");
        Assert.Contains(parsedDetail.Metadata, item => item.DisplayName == "Status" && item.Key == "ongoing");
        Assert.Contains(parsedDetail.Metadata, item => item.DisplayName == "Author" && item.Key == "Denfee");
        Assert.Contains(parsedDetail.Metadata, item => item.DisplayName == "Artist" && item.Key == "Meona");
        var chapter = Assert.Single(chapters);
        Assert.Equal("7395790b-5358-41a0-a83b-68aa28e6fcd3", chapter.Identity.ChapterId);
        Assert.Equal("89", chapter.Identity.ChapterNumber);
        Assert.Equal("Chapter 89", chapter.DisplayName);
    }

    [Fact]
    public void ThunderHtmlPayloadAndSitemapIndexProduceIndependentTitleAndJpegManifestInputs()
    {
        const string titleHtml = """
            <h1>Global Martial Arts</h1><img src="https://en-thunderscans.com/wp-content/uploads/2026/07/cover.jpg">
            <div id="chapterlist"><ul>
            <li data-num="371"><a href="/global-martial-arts-chapter-371/">Chapter 371</a></li>
            <li data-num="372"><a href="/global-martial-arts-chapter-372/">Chapter 372</a></li>
            </ul></div>
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
    public async Task ThunderChaptersReadNumbersAndRoutesDirectlyFromTheChapterListRows()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/comics/0086250808-i-got-the-weakest-class-dragon-tamer/" => Html("""
                    <div id="chapterlist"><ul>
                    <li data-num="50"><a data-id="locked">Chapter 50</a></li>
                    <li data-num="286"><a href="/i-got-the-weakest-class-dragon-tamer-chapter-286/">Chapter 286</a></li>
                    <li data-num="8"><a href="/1482765166-i-got-the-weakest-class-dragon-tamer-chapter-8/">Chapter 8</a></li>
                    <li data-num="7"><a href="/1482765166-i-got-the-weakest-class-dragon-tamer-7/">Chapter 7</a></li>
                    <li data-num="06"><a href="/1482765166-i-got-the-weakest-class-dragon-tamer-06/">Chapter 06</a></li>
                    <li data-num="1"><a href="/1482765166-i-got-the-weakest-class-dragon-tamer-1/">Chapter 1</a></li>
                    </ul></div>
                    """),
                "/1482765166-i-got-the-weakest-class-dragon-tamer-06/" => Html("""
                    <script>ts_reader.run({"sources":[{"images":[
                    "https://en-thunderscans.com/wp-content/uploads/manga/a/0001_x.jpg"
                    ]}]});</script>
                    """),
                _ => throw new Xunit.Sdk.XunitException("Unexpected request: " + request.RequestUri),
            };
        }));
        var source = new ThunderScansSource(client);
        var title = new RemoteTitleIdentity(ThunderScansContract.SourceId, "0086250808-i-got-the-weakest-class-dragon-tamer", "0086250808-i-got-the-weakest-class-dragon-tamer", "0086250808-i-got-the-weakest-class-dragon-tamer");

        var chapters = await source.GetChaptersAsync(title, ThunderScansSource.Group.Identity, CancellationToken.None);
        var chapterSix = Assert.Single(chapters, chapter => chapter.Identity.ChapterNumber == "6");
        var manifest = await source.GetManifestAsync(chapterSix.Identity, CancellationToken.None);

        Assert.Equal(["1", "6", "7", "8", "286"], chapters.Select(chapter => chapter.Identity.ChapterNumber));
        Assert.Equal("1482765166-i-got-the-weakest-class-dragon-tamer-1", chapters[0].Identity.ChapterId);
        Assert.Equal("1482765166-i-got-the-weakest-class-dragon-tamer-06", chapterSix.Identity.ChapterId);
        Assert.Single(manifest.Pages);
        Assert.Equal([
            "/comics/0086250808-i-got-the-weakest-class-dragon-tamer/",
            "/1482765166-i-got-the-weakest-class-dragon-tamer-06/",
        ], requests);
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
