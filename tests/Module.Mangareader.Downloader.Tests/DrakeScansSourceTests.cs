using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.FilterSearch.DrakeScans;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.DrakeScans;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

public sealed class DrakeScansSourceTests
{
    [Fact]
    public void CatalogAndGenreJsonAreNormalizedWithoutGuessingIdentity()
    {
        const string catalog = """
            {"data":[{"id":"series-1","slug":"beast-evolution","urlSlug":"beast-evolution",
              "title":"Beast Evolution","coverImage":"/uploads/series/beast-evolution/cover.webp",
              "type":"MANHUA","status":"ONGOING","rating":9.5,"isMature":false,
              "dmcaTakenDown":false,"genres":[{"genre":{"slug":"action"}}],
              "chapters":[{"id":"chapter-81","number":81,"title":"81","isLocked":false,
                "coinPrice":0,"isFree":true}] }],
             "meta":{"total":52,"page":2,"limit":24,"totalPages":3,"hasMore":true}}
            """;
        const string genres = """
            {"genres":[{"id":"g1","name":"Action","slug":"action"},
                       {"id":"g2","name":"Fantasy","slug":"fantasy"}]}
            """;

        var page = DrakeScansJsonParser.ParseCatalog(catalog, 2);
        var options = DrakeScansJsonParser.ParseGenres(genres);

        Assert.Equal(52, page.Total);
        Assert.Equal(2, page.Page);
        Assert.True(page.HasMore);
        var title = Assert.Single(page.Items);
        Assert.Equal(new RemoteTitleIdentity("drake-scans", "beast-evolution", "series-1", "beast-evolution"), title.Identity);
        Assert.Equal("https://drakecomic.net/uploads/series/beast-evolution/cover.webp", title.CoverUrl);
        Assert.Equal("Chapter 81", title.LatestChapterLabel);
        Assert.Equal(["Action", "Fantasy"], options.Select(option => option.DisplayName));
    }

    [Fact]
    public void RscDetailAndChaptersPreserveMetadataOrderAndAccessBoundary()
    {
        const string rsc = """
            2:["$","component",null,{"series":{"id":"series-1","slug":"beast-evolution",
            "title":"Beast Evolution","description":"A beast-taming story.",
            "coverImage":"/uploads/series/beast-evolution/cover.webp","type":"MANHUA",
            "status":"ONGOING","rating":9.5,"genres":[{"name":"Action","slug":"action"}]},
            "chapters":[{"id":"c3","number":3,"title":"Chapter 3","isLocked":true,
            "coinPrice":50,"hasAccess":true},{"id":"c2","number":2,"title":"Chapter 2",
            "isLocked":true,"coinPrice":50,"hasAccess":false},{"id":"c1","number":1,
            "title":"Chapter 1","isLocked":false,"coinPrice":0,"hasAccess":true}],
            "currentPage":1,"totalPages":1}]
            """;
        var title = Title();

        var detail = DrakeScansRscParser.ParseDetail(rsc, title);
        var chapters = DrakeScansRscParser.ParseChapters(rsc, title, DrakeScansSource.Group.Identity);

        Assert.Equal("A beast-taming story.", detail.Description);
        Assert.Equal(["Action"], detail.Genres.Select(item => item.DisplayName));
        Assert.Contains(detail.Metadata, item => item.Key == "Type" && item.DisplayName == "MANHUA");
        Assert.Contains(detail.Metadata, item => item.Key == "Status" && item.DisplayName == "ONGOING");
        Assert.Equal(["1", "3"], chapters.Select(item => item.Identity.ChapterNumber));
        Assert.Equal([0, 1], chapters.Select(item => item.OrderIndex));
    }

    [Fact]
    public async Task SourceLoadsEveryChapterPageBeforeReturningTheAccessibleList()
    {
        var handler = new RoutingHandler(request =>
        {
            var page = request.RequestUri?.Query.Contains("page=2", StringComparison.Ordinal) == true ? 2 : 1;
            return page == 1
                ? Rsc("""
                    2:["$",null,{"chapters":[
                    {"id":"c1","number":1,"title":"1","isLocked":false,"hasAccess":true},
                    {"id":"c2","number":2,"title":"2","isLocked":true,"hasAccess":false}],
                    "currentPage":1,"totalPages":2}]
                    """)
                : Rsc("""
                    2:["$",null,{"chapters":[
                    {"id":"c101","number":101,"title":"101","isLocked":false,"hasAccess":true}],
                    "currentPage":2,"totalPages":2}]
                    """);
        });
        var source = new DrakeScansSource(new HttpClient(handler));

        var chapters = await source.GetChaptersAsync(
            Title(), DrakeScansSource.Group.Identity, CancellationToken.None);

        Assert.Equal(["1", "101"], chapters.Select(chapter => chapter.Identity.ChapterNumber));
        Assert.Equal([0, 1], chapters.Select(chapter => chapter.OrderIndex));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("page=2", handler.Requests[1].RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void RscManifestUsesOnlyTopLevelFullPagesAndRejectsForeignHosts()
    {
        const string valid = """
            4:["$","reader",null,{"chapter":{"id":"c81","number":81,"title":"81",
            "pages":[{"id":"p1","pageNumber":1,"imageUrl":"/uploads/series/beast-evolution/0081/p-one.webp",
            "strips":[{"imageUrl":"/uploads/series/beast-evolution/0081/s-one.webp"}]},
            {"id":"p2","pageNumber":2,"imageUrl":"https://drakecomic.net/uploads/series/beast-evolution/0081/p-two.webp",
            "strips":[{"imageUrl":"/uploads/series/beast-evolution/0081/s-two.webp"}]}]}}]
            """;
        const string foreign = """
            4:["$",null,{"chapter":{"pages":[{"id":"p1","pageNumber":1,
            "imageUrl":"https://example.com/uploads/series/beast-evolution/0081/p-one.webp"}]}}]
            """;

        var pages = DrakeScansRscParser.ParsePages(valid, "beast-evolution");

        Assert.Equal([1, 2], pages.Select(page => page.PageNumber));
        Assert.All(pages, page => Assert.Contains("/p-", page.Url, StringComparison.Ordinal));
        Assert.DoesNotContain(pages, page => page.Url.Contains("/s-", StringComparison.Ordinal));
        Assert.Throws<DrakeScansContractException>(
            () => DrakeScansRscParser.ParsePages(foreign, "beast-evolution"));
    }

    [Fact]
    public async Task SourceSerializesSearchFiltersRscHeadersAndStableManifest()
    {
        var handler = new RoutingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/series" => Json("""
                {"data":[],"meta":{"total":0,"page":2,"limit":24,"totalPages":0,"hasMore":false}}
                """),
            "/series/comic/beast-evolution/chapter/81" => Rsc("""
                4:["$",null,{"chapter":{"pages":[{"id":"p1","pageNumber":1,
                "imageUrl":"/uploads/series/beast-evolution/0081/p-one.webp"}]}}]
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var source = new DrakeScansSource(new HttpClient(handler));
        var filter = new DrakeScansBrowseQuery { Genres = ["action", "fantasy"], Statuses = ["ONGOING"] };

        await source.BrowseAsync(new RemoteBrowseRequest("  Beast  ", 2, filter), CancellationToken.None);
        var browse = handler.Requests.Single(request => request.RequestUri?.AbsolutePath == "/api/series");
        Assert.Contains("page=2", browse.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("limit=24", browse.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("q=Beast", browse.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("genre=action%2Cfantasy", browse.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("status=ONGOING", browse.RequestUri?.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("sort=", browse.RequestUri?.Query, StringComparison.Ordinal);

        var queuedTitle = Title() with { Slug = string.Empty };
        var queuedChapter = Chapter("81") with { Title = queuedTitle };
        var manifest = await source.GetManifestAsync(queuedChapter, CancellationToken.None);
        var rsc = handler.Requests.Single(request => request.RequestUri?.AbsolutePath.EndsWith("/chapter/81", StringComparison.Ordinal) == true);
        Assert.Equal("1", rsc.Headers.GetValues("RSC").Single());
        Assert.Contains("text/x-component", rsc.Headers.GetValues("Accept"));
        Assert.Equal("https://drakecomic.net/series/comic/beast-evolution/chapter/81", manifest.RequestHeaders["Referer"]);
        Assert.StartsWith("sha256:", manifest.ManifestHash, StringComparison.Ordinal);
        Assert.Single(manifest.Pages);
    }

    [Fact]
    public async Task SourceRejectsUnexpectedContentAndInvalidImageBytes()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>maintenance</html>", Encoding.UTF8, "text/html"),
        });
        var source = new DrakeScansSource(new HttpClient(handler));

        await Assert.ThrowsAsync<DrakeScansContractException>(
            () => source.BrowseAsync(new RemoteBrowseRequest(null, 1, null), CancellationToken.None));
        await Assert.ThrowsAsync<DrakeScansContractException>(
            () => source.TransformPageAsync(
                new RemotePage(0, "bad", "https://drakecomic.net/bad", null, null),
                [1, 2, 3],
                CancellationToken.None));
    }

    [Fact]
    public void RegistryAndFilterContributionRemainProviderOwned()
    {
        var root = Path.Combine(Path.GetTempPath(), "citadel-drake-registry-" + Guid.NewGuid().ToString("N"));
        using var browser = new DownloaderPyHostClient(root);
        var registry = MangaSourceRegistry.CreateDefault(browser);

        Assert.Equal(["comix", "cucumber-manga", "drake-scans"], registry.Sources.Select(source => source.Id));
        var drake = registry.Require("drake-scans");
        var state = RunSta(() =>
        {
            var panel = Assert.IsType<DrakeScansFilterPanel>(drake.CreateFilters().CreatePanel());
            var snapshot = Assert.IsType<DrakeScansBrowseQuery>(panel.Snapshot(null));
            return (snapshot.Genres.ToArray(), snapshot.Statuses.ToArray());
        });
        Assert.Empty(state.Item1);
        Assert.Empty(state.Item2);
    }

    private static RemoteTitleIdentity Title() => new(
        DrakeScansContract.SourceId,
        "beast-evolution",
        "series-1",
        "beast-evolution");

    private static RemoteChapterIdentity Chapter(string number) => new(
        DrakeScansContract.SourceId,
        Title(),
        number,
        number,
        DrakeScansSource.Group.Identity);

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Rsc(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/x-component"),
    };

    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result!;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
