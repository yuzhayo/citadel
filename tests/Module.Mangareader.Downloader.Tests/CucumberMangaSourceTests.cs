using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.CucumberManga;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

public sealed class CucumberMangaSourceTests
{
    [Fact]
    public void BrowseParserKeepsProviderIdentityLargestCoverAndLatestChapter()
    {
        const string html = """
            <div class="page-item-detail manga">
              <div data-post-id="4657"><img data-src="https://cucumbermanga.com/small.jpg"
                data-srcset="https://cucumbermanga.com/small.jpg 175w, https://cucumbermanga.com/large.jpg 700w"></div>
              <div class="post-title"><a href="https://cucumbermanga.com/manga/yang-ilwoo-and-me/"> Yang Ilwoo and Me </a></div>
              <div class="list-chapter"><div class="chapter-item"><a> Chapter 31 </a></div></div>
            </div>
            """;

        var page = CucumberMangaHtmlParser.ParseBrowse(html, 1);

        var title = Assert.Single(page.Items);
        Assert.Equal("cucumber-manga", title.Identity.SourceId);
        Assert.Equal("4657", title.Identity.TitleId);
        Assert.Equal("yang-ilwoo-and-me", title.Identity.TitleHid);
        Assert.Equal("Yang Ilwoo and Me", title.DisplayName);
        Assert.Equal("https://cucumbermanga.com/large.jpg", title.CoverUrl);
        Assert.Equal("Chapter 31", title.LatestChapterLabel);
    }

    [Fact]
    public void DetailParserReadsDescriptionTagsMetadataAndOriginalCover()
    {
        const string html = """
            <link rel="shortlink" href="https://cucumbermanga.com/?p=4657">
            <div class="post-title"><h1>Yang Ilwoo and Me</h1></div>
            <div class="summary_image"><img data-src="https://cucumbermanga.com/193.jpg"
              data-srcset="https://cucumbermanga.com/193.jpg 193w, https://cucumbermanga.com/original.jpg 900w"></div>
            <div class="description-summary"><div class="summary__content"><p>First paragraph.</p><p>Second paragraph.</p></div></div>
            <div class="post-content_item"><div class="summary-heading"><h5>Tag(s)</h5></div>
              <div class="summary-content"><div class="tags-content"><a href="/manga-tag/adult/">Adult</a><a href="/manga-tag/yaoi/">Yaoi</a></div></div></div>
            <div class="post-content_item"><div class="summary-heading"><h5>Status</h5></div><div class="summary-content">OnGoing</div></div>
            """;
        var identity = new RemoteTitleIdentity("cucumber-manga", "4657", "yang-ilwoo-and-me", "yang-ilwoo-and-me");

        var detail = CucumberMangaHtmlParser.ParseTitleDetail(html, identity);

        Assert.Equal("https://cucumbermanga.com/original.jpg", detail.Summary.CoverUrl);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", detail.Description);
        Assert.Equal(["Adult", "Yaoi"], detail.Genres.Select(item => item.DisplayName));
        Assert.Contains(detail.Metadata, item => item.Key == "Status" && item.DisplayName == "OnGoing");
    }

    [Fact]
    public void ChapterAndManifestParsersPreserveProviderOrderAndPageOrder()
    {
        const string chapters = """
            <ul>
              <li class="wp-manga-chapter"><a href="https://cucumbermanga.com/manga/yang-ilwoo-and-me/chapter-31/">Chapter 31</a></li>
              <li class="wp-manga-chapter"><a href="https://cucumbermanga.com/manga/yang-ilwoo-and-me/n-a-2/">0</a></li>
            </ul>
            """;
        const string pages = """
            <div class="reading-content">
              <div class="page-break"><img data-src=" https://cucumbermanga.com/uploads/002.jpg "></div>
              <div class="page-break"><img data-lazy-src="https://cucumbermanga.com/uploads/003.webp"></div>
            </div>
            """;
        var title = new RemoteTitleIdentity("cucumber-manga", "4657", "yang-ilwoo-and-me", "yang-ilwoo-and-me");
        var group = CucumberMangaSource.Group.Identity;

        var parsedChapters = CucumberMangaHtmlParser.ParseChapters(chapters, title, group);
        var parsedPages = CucumberMangaHtmlParser.ParsePages(pages);

        Assert.Equal(["chapter-31", "n-a-2"], parsedChapters.Select(item => item.Identity.ChapterId));
        Assert.Equal(["31", "0"], parsedChapters.Select(item => item.Identity.ChapterNumber));
        Assert.Equal(["https://cucumbermanga.com/uploads/002.jpg", "https://cucumbermanga.com/uploads/003.webp"], parsedPages);
    }

    [Fact]
    public async Task SearchUsesCapturedMadaraAjaxContract()
    {
        var handler = new RecordingHandler("""
            <div class="page-item-detail"><div data-post-id="1"></div>
              <div class="post-title"><a href="https://cucumbermanga.com/manga/result/">Result</a></div></div>
            """);
        var source = new CucumberMangaSource(new HttpClient(handler));

        var page = await source.BrowseAsync(new RemoteBrowseRequest("  lover boy  ", 2, null), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://cucumbermanga.com/wp-admin/admin-ajax.php", handler.Uri?.ToString());
        Assert.Contains("action=madara_load_more", handler.Body, StringComparison.Ordinal);
        Assert.Contains("page=1", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Bs%5D=lover+boy", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("vars%5Bmeta_key%5D=_latest_update", handler.Body, StringComparison.Ordinal);
        Assert.True(handler.HasXmlHttpRequestHeader);
    }

    [Fact]
    public async Task DefaultBrowseUsesLatestUpdateAndTwentyFiveItemPageSize()
    {
        var handler = new RecordingHandler(string.Empty);
        var source = new CucumberMangaSource(new HttpClient(handler));

        await source.BrowseAsync(new RemoteBrowseRequest(null, 1, null), CancellationToken.None);

        Assert.Contains("vars%5Bposts_per_page%5D=25", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Bmeta_key%5D=_latest_update", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Border%5D=DESC", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterRequestUsesCapturedMadaraTaxonomyAndMetaKeys()
    {
        var handler = new RecordingHandler(string.Empty);
        var source = new CucumberMangaSource(new HttpClient(handler));
        var filter = new CucumberMangaBrowseQuery
        {
            SortKey = "alphabet",
            Statuses = ["on-going"],
            Genres = ["romance", "yaoi"],
            AdultMode = CucumberMangaAdultMode.Only,
            Author = "Jane Doe",
            Artist = "John Doe",
            ReleaseYear = "2026",
        };

        await source.BrowseAsync(new RemoteBrowseRequest(null, 1, filter), CancellationToken.None);

        Assert.Contains("vars%5Borderby%5D=post_title", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Bmeta_query%5D%5B1%5D%5Bkey%5D=_wp_manga_status", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Bmeta_query%5D%5B2%5D%5Bkey%5D=manga_adult_content", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Btax_query%5D%5B0%5D%5Btaxonomy%5D=wp-manga-author", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Btax_query%5D%5B3%5D%5Btaxonomy%5D=wp-manga-genre", handler.Body, StringComparison.Ordinal);
        Assert.Contains("vars%5Btax_query%5D%5B3%5D%5Bterms%5D%5B1%5D=yaoi", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultRegistryAddsCucumberAfterUntouchedComix()
    {
        var root = Path.Combine(Path.GetTempPath(), "citadel-cucumber-registry-" + Guid.NewGuid().ToString("N"));
        using var browser = new DownloaderPyHostClient(root);

        var registry = MangaSourceRegistry.CreateDefault(browser);

        Assert.Equal(["comix", "cucumber-manga"], registry.Sources.Take(2).Select(source => source.Id));
    }

    private sealed class RecordingHandler(string html) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public bool HasXmlHttpRequestHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HasXmlHttpRequestHeader = request.Headers.TryGetValues("X-Requested-With", out var values)
                && values.Contains("XMLHttpRequest", StringComparer.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
        }
    }
}
