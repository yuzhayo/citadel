using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.WeebCentral;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

public sealed class WeebCentralSourceTests
{
    [Fact]
    public void BrowseParserReadsProviderIdentityCoverPairAndMoreSentinel()
    {
        const string html = """
            <section><article class="bg-base-300 flex gap-4 p-4">
              <section><a href="https://weebcentral.com/series/01JABC/Kobato">
                <picture><source srcset="https://temp.compsci88.com/cover/normal/01JABC.webp" /></picture>
                </a></section>
              <section><div><span><a href="https://weebcentral.com/series/01JABC/Kobato" class="line-clamp-1 link link-hover">Kobato.</a></span></div></section>
            </article>
            <article class="bg-base-300 flex gap-4 p-4">
              <section><a href="https://weebcentral.com/series/01JDEF/Solo-Leveling">
                <picture><img src="https://temp.compsci88.com/cover/fallback/01JDEF.jpg" alt="Solo Leveling cover" /></picture>
                </a></section>
            </article>
            <button hx-get="/search/data?limit=32&amp;offset=32&amp;display_mode=Full+Display"><span>View More Results...</span></button>
            """;

        var page = WeebCentralHtmlParser.ParseGrid(html, 1);

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        var first = page.Items[0];
        Assert.Equal("weebcentral", first.Identity.SourceId);
        Assert.Equal("01JABC", first.Identity.TitleId);
        Assert.Equal("Kobato.", first.DisplayName);
        Assert.Equal("https://temp.compsci88.com/cover/normal/01JABC.webp", first.CoverUrl);
        Assert.Single(first.CoverFallbackUrls);
        Assert.Contains("https://temp.compsci88.com/cover/fallback/01JABC.jpg", first.CoverFallbackUrls);
        var second = page.Items[1];
        Assert.Equal("Solo Leveling", second.DisplayName);
    }

    [Fact]
    public void BrowseParserDeduplicatesCoverAndTitleAnchors()
    {
        const string html = """
            <article class="bg-base-300 flex gap-4 p-4">
              <section><a href="https://weebcentral.com/series/01JABC/Kobato"></a></section>
              <section><a href="https://weebcentral.com/series/01JABC/Kobato" class="line-clamp-1 link link-hover">Kobato.</a></section>
            </article>
            """;

        var page = WeebCentralHtmlParser.ParseGrid(html, 1);

        var single = Assert.Single(page.Items);
        Assert.Equal("01JABC", single.Identity.TitleId);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void DetailParserReadsDescriptionTagsMetadataAndCanonicalCover()
    {
        const string html = """
            <h1>One Piece</h1>
            <ul class="flex flex-col gap-4">
              <li><strong>Author(s): </strong><span><a href="https://weebcentral.com/search?author=ODA+Eiichiro">ODA Eiichiro</a></span></li>
              <li><strong>Tags(s): </strong><span><a href="https://weebcentral.com/search?included_tag=Action">Action</a>,</span><span><a href="https://weebcentral.com/search?included_tag=Adventure">Adventure</a></span></li>
              <li><strong>Type: </strong><a href="https://weebcentral.com/search?included_type=Manga">Manga</a></li>
              <li><strong>Description</strong><p class="whitespace-pre-wrap break-words">Pirates.</p></li>
            </ul>
            """;
        var identity = new RemoteTitleIdentity("weebcentral", "01JABC", "01JABC", "One-Piece");

        var detail = WeebCentralHtmlParser.ParseTitleDetail(html, identity);

        Assert.Equal("One Piece", detail.Summary.DisplayName);
        Assert.Equal("Pirates.", detail.Description);
        Assert.Equal(["Action", "Adventure"], detail.Genres.Select(item => item.DisplayName));
        Assert.Contains(detail.Metadata, item => item.Key == "Type" && item.DisplayName == "Manga");
        Assert.DoesNotContain(detail.Metadata, item => item.Key.StartsWith("Tag", StringComparison.Ordinal));
        Assert.Equal("https://temp.compsci88.com/cover/normal/01JABC.webp", detail.Summary.CoverUrl);
    }

    [Fact]
    public void ChapterAndManifestParsersPreserveDocumentOrderAndPageOrder()
    {
        const string chapters = """
            <a href="/chapters/01M3DV"><span class="">Chapter 1194</span></a>
            <a href="https://weebcentral.com/chapters/01M27C"><span class="">Special 2</span></a>
            """;
        const string pages = """
            <section id="chapter-images">
              <img src="https://hot.planeptune.us/manga/One-Piece/0003-001.png" alt="Page 1" />
              <img src="https://hot.planeptune.us/manga/One-Piece/0003-002.png" alt="Page 2" />
            </section>
            """;
        var title = new RemoteTitleIdentity("weebcentral", "01JABC", "01JABC", "One-Piece");
        var group = WeebCentralSource.Group.Identity;

        var parsedChapters = WeebCentralHtmlParser.ParseChapters(chapters, title, group);
        var parsedPages = WeebCentralHtmlParser.ParsePages(pages);

        Assert.Equal(["01M3DV", "01M27C"], parsedChapters.Select(item => item.Identity.ChapterId));
        Assert.Equal(["1194", "2"], parsedChapters.Select(item => item.Identity.ChapterNumber));
        Assert.Equal(
            ["https://hot.planeptune.us/manga/One-Piece/0003-001.png",
             "https://hot.planeptune.us/manga/One-Piece/0003-002.png"],
            parsedPages);
    }

    [Fact]
    public async Task SearchPostsGridRequestWithRelevanceSort()
    {
        var handler = new RecordingHandler("""
            <button hx-get="/search/data?limit=32&amp;offset=32&amp;display_mode=Full+Display"><span>View More Results...</span></button>
            """);
        var source = new WeebCentralSource(new HttpClient(handler));

        var page = await source.BrowseAsync(new RemoteBrowseRequest("  one piece  ", 2, null), CancellationToken.None);

        Assert.True(page.HasMore);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://weebcentral.com/search/data", handler.PathOnly);
        Assert.Equal("32", handler.Query["limit"]);
        Assert.Equal("Full Display", handler.Query["display_mode"]);
        Assert.Equal("32", handler.Query["offset"]);
        Assert.Equal("one piece", handler.Query["text"]);
        Assert.Equal("Best Match", handler.Query["sort"]);
        Assert.Equal("Full Display", handler.Query["display_mode"]);
        Assert.Equal("Any", handler.Query["adult"]);
        Assert.True(handler.HasHtmxHeader);
    }

    [Fact]
    public async Task BrowseWithoutKeywordSendsPanelSortAndNoText()
    {
        var handler = new RecordingHandler("<div>plain browse</div>");
        var source = new WeebCentralSource(new HttpClient(handler));
        var filter = new WeebCentralBrowseQuery
        {
            SortKey = "Popularity",
            Order = "Descending",
        };

        var page = await source.BrowseAsync(new RemoteBrowseRequest(null, 1, filter), CancellationToken.None);

        Assert.False(page.HasMore);
        Assert.Equal("Popularity", handler.Query["sort"]);
        Assert.Equal("Descending", handler.Query["order"]);
        Assert.False(handler.Query.ContainsKey("text"));
        Assert.Equal("0", handler.Query["offset"]);
        Assert.Equal("32", handler.Query["limit"]);
    }

    [Fact]
    public async Task ChaptersReadFromFullListEndpointWithoutDisplaySlug()
    {
        var handler = new RecordingHandler("""
            <a href="/chapters/01M3DV"><span>Chapter 1194</span></a>
            """);
        var source = new WeebCentralSource(new HttpClient(handler));
        var title = new RemoteTitleIdentity("weebcentral", "01JABC", "01JABC", "One-Piece");

        var chapters = await source.GetChaptersAsync(
            title,
            WeebCentralSource.Group.Identity,
            CancellationToken.None);

        Assert.Equal("https://weebcentral.com/series/01JABC/full-chapter-list", handler.PathOnly);
        Assert.DoesNotContain("One-Piece/full-chapter-list", handler.PathOnly, StringComparison.Ordinal);
        Assert.Equal("01M3DV", Assert.Single(chapters).Identity.ChapterId);
    }

    [Fact]
    public async Task ManifestBuildsLongStripRequestWithChapterReferer()
    {
        var handler = new RecordingHandler("""
            <section id="chapter-images">
              <img src="https://hot.planeptune.us/manga/One-Piece/0003-001.png" alt="Page 1" />
            </section>
            """);
        var source = new WeebCentralSource(new HttpClient(handler));
        var title = new RemoteTitleIdentity("weebcentral", "01JABC", "01JABC", "One-Piece");
        var chapter = new RemoteChapterIdentity(
            "weebcentral", title, "01J76XYYR7FFXEJKK4J072VTM4", "3", WeebCentralSource.Group.Identity);

        var manifest = await source.GetManifestAsync(chapter, CancellationToken.None);

        var url = Assert.Single(manifest.Pages).Url;
        Assert.Equal("https://hot.planeptune.us/manga/One-Piece/0003-001.png", url);
        Assert.Equal("https://weebcentral.com/chapters/01J76XYYR7FFXEJKK4J072VTM4", manifest.RequestHeaders["Referer"]);
        Assert.StartsWith("sha256:", manifest.ManifestHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMissingPagesWithoutHittingAnotherEndpoint()
    {
        var handler = new RecordingHandler("<div>No reader here.</div>");
        var source = new WeebCentralSource(new HttpClient(handler));
        var title = new RemoteTitleIdentity("weebcentral", "01JABC", "01JABC", "One-Piece");
        var chapter = new RemoteChapterIdentity(
            "weebcentral", title, "01J76XYYR7FFXEJKK4J072VTM4", "3", WeebCentralSource.Group.Identity);

        var error = await Assert.ThrowsAsync<WeebCentralContractException>(
            () => source.GetManifestAsync(chapter, CancellationToken.None));

        Assert.Contains("no pages", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FormatDetectorTakesPayloadOverCdnExtension()
    {
        var page = new RemotePage(
            0,
            "weebcentral-page-0",
            "https://hot.planeptune.us/manga/One-Piece/0003-001.png",
            null,
            null);

        var source = new WeebCentralSource(new HttpClient(new RecordingHandler(string.Empty)));
        Assert.Equal("jpg", (await source.TransformPageAsync(page, [0xFF, 0xD8, 0xFF, 0x00], CancellationToken.None).ConfigureAwait(false)).Format);
        Assert.Equal("png", (await source.TransformPageAsync(page, [0x89, 0x50, 0x4E, 0x47], CancellationToken.None).ConfigureAwait(false)).Format);
        await Assert.ThrowsAsync<WeebCentralContractException>(() =>
            source.TransformPageAsync(page, [0x00, 0x01, 0x02], CancellationToken.None));
    }

    [Fact]
    public void RegistryAddsWeebCentralAfterUntouchedThunderScans()
    {
        var root = Path.Combine(Path.GetTempPath(), "citadel-weebcentral-registry-" + Guid.NewGuid().ToString("N"));
        using var browser = new DownloaderPyHostClient(root);

        var registry = MangaSourceRegistry.CreateDefault(browser);

        Assert.Equal(
            ["comix", "cucumber-manga", "drake-scans", "asura-scans", "thunder-scans", "weebcentral"],
            registry.Sources.Select(source => source.Id));
        Assert.Equal("WeebCentral", registry.Require("weebcentral").DisplayName);
    }

    private sealed class RecordingHandler(string html) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public Dictionary<string, string> Query { get; private set; } = new(StringComparer.Ordinal);
        public bool HasHtmxHeader { get; private set; }

        public string PathOnly => Uri is null ? string.Empty : Uri.GetLeftPart(UriPartial.Path);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            Query = Uri is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]), StringComparer.Ordinal);
            HasHtmxHeader = request.Headers.TryGetValues("HX-Request", out var values) && values.Contains("true");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            });
        }
    }
}

