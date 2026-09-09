using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Local-data verification boundary: pure query/filter/sort/page, enrichment
/// replace, and bounded/valid cover cache through fake HTTP responses. No
/// browser, no live Comix, no full crawl, no UI controls.
/// </summary>
public sealed class CatalogMirrorLocalDataTests : IDisposable
{
    // Canonical 1x1 transparent PNG; decoders accept it universally.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.CatalogMirror.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SearchCoversTitlesAndAlternateTitlesCaseInsensitively()
    {
        var index = Seed([
            Item("a", title: "Lying Girlfriend", alts: ["Kagemori-san"]),
            Item("b", title: "Horse Keeper"),
            Item("c", title: "Something Else", alts: ["Secret Alt"]),
        ]);

        Assert.Equal(["a"], Ids(index.Execute(Query(search: "lying"))));
        Assert.Equal(["a"], Ids(index.Execute(Query(search: "KAGEMORI"))));
        Assert.Equal(["c"], Ids(index.Execute(Query(search: "secret alt"))));
        Assert.Equal(3, index.Execute(Query(search: "")).TotalResults);
        Assert.Equal(3, index.Execute(Query(search: "  ")).TotalResults);
    }

    [Fact]
    public void SearchTreatsWildcardsLiterally()
    {
        var index = Seed([
            Item("a", title: "100% real"),
            Item("b", title: "under_score"),
            Item("c", title: @"back\slash"),
            Item("d", title: "plain title"),
        ]);

        Assert.Equal(["a"], Ids(index.Execute(Query(search: "100%"))));
        Assert.Equal(["b"], Ids(index.Execute(Query(search: "_"))));
        Assert.Equal(["c"], Ids(index.Execute(Query(search: "\\"))));
        Assert.Equal(["a"], Ids(index.Execute(Query(search: "100"))));
    }

    [Fact]
    public void FiltersApplyExactly()
    {
        var index = Seed([
            Item("a", rating: "safe", type: "manga", status: "releasing", language: "en", year: 2020, latest: 5),
            Item("b", rating: "suggestive", type: "manhwa", status: "finished", language: "ja", year: 2018, latest: 12),
            Item("c", rating: "safe", type: "manhwa", status: "releasing", language: "en", year: null, latest: null),
        ]);

        Assert.Equal(["a", "c"], Ids(index.Execute(Query(filter: Filter(ratings: [CatalogContentRating.Safe])))));
        Assert.Equal(["b", "c"], Ids(index.Execute(Query(filter: Filter(types: ["MANHWA"])))));
        Assert.Equal(["b"], Ids(index.Execute(Query(filter: Filter(statuses: ["finished"])))));
        Assert.Equal(["b"], Ids(index.Execute(Query(filter: Filter(languages: ["ja"])))));
        Assert.Equal(["a", "b"], Ids(index.Execute(Query(filter: Filter(yearFrom: 2018, yearTo: 2020)))));
        Assert.Equal(["a"], Ids(index.Execute(Query(filter: Filter(yearFrom: 2019)))));
        Assert.Equal(["b"], Ids(index.Execute(Query(filter: Filter(minLatestChapter: 10)))));
    }

    [Fact]
    public void SortsOrderDeterministicallyWithNullsLast()
    {
        var index = Seed([
            Item("b", title: "b", year: 2020, latest: 5),
            Item("a", title: "a", year: null, latest: null),
            Item("c", title: "c", year: 2022, latest: 9),
        ]);

        Assert.Equal(["a", "b", "c"], Ids(index.Execute(Query(sort: CatalogTitleSort.TitleAsc))));
        Assert.Equal(["c", "b", "a"], Ids(index.Execute(Query(sort: CatalogTitleSort.TitleDesc))));
        Assert.Equal(["c", "b", "a"], Ids(index.Execute(Query(sort: CatalogTitleSort.YearNewest))));
        Assert.Equal(["b", "c", "a"], Ids(index.Execute(Query(sort: CatalogTitleSort.YearOldest))));
        Assert.Equal(["c", "b", "a"], Ids(index.Execute(Query(sort: CatalogTitleSort.LatestChapterHighest))));
        Assert.Equal(["b", "c", "a"], Ids(index.Execute(Query(sort: CatalogTitleSort.LatestChapterLowest))));
    }

    [Fact]
    public void PagingUsesFixedPageSizeAndClamps()
    {
        var items = Enumerable.Range(0, 45).Select(number => Item("t" + number)).ToArray();
        var index = Seed(items);

        var first = index.Execute(Query(page: 1));
        Assert.Equal(45, first.TotalResults);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(40, first.Items.Count);

        var second = index.Execute(Query(page: 2));
        Assert.Equal(2, second.Page);
        Assert.Equal(5, second.Items.Count);

        var clamped = index.Execute(Query(page: 99));
        Assert.Equal(2, clamped.Page);
        Assert.Equal(5, clamped.Items.Count);

        var zero = index.Execute(Query(page: 0));
        Assert.Equal(1, zero.Page);

        var empty = Seed([]).Execute(Query(page: 1));
        Assert.Equal(0, empty.TotalResults);
        Assert.Equal(1, empty.TotalPages);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public void GenerationGuardKeepsLatestQueryWins()
    {
        var index = Seed([Item("a")]);

        var first = index.BeginQuery();
        var second = index.BeginQuery();

        Assert.False(index.IsLatest(first));
        Assert.True(index.IsLatest(second));
    }

    [Fact]
    public void LargeSnapshotReturnsOnePageWithoutMaterializingViews()
    {
        var items = Enumerable.Range(0, 20_000).Select(number => Item("t" + number)).ToArray();
        var index = Seed(items);

        var page = index.Execute(Query(page: 1));

        Assert.Equal(20_000, page.TotalResults);
        Assert.Equal(500, page.TotalPages);
        Assert.Equal(CatalogMirrorPaging.DefaultPageSize, page.Items.Count);
        Assert.All(page.Items, item => Assert.IsType<CatalogSnapshotItem>(item));
    }

    [Fact]
    public async Task EnrichmentReplacesOneFileRatherThanAppending()
    {
        var store = new CatalogEnrichmentStore(new CatalogMirrorPaths(_root));
        var first = new CatalogTitleEnrichment("comix", "a", "h-a", "Title A", "Old", DateTimeOffset.UtcNow);
        var second = first with { Synopsis = "New" };

        await store.WriteAsync(first, CancellationToken.None);
        await store.WriteAsync(second, CancellationToken.None);

        var read = await store.ReadAsync("comix", "a", CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal("New", read.Synopsis);

        var directory = Path.GetDirectoryName(
            new CatalogMirrorPaths(_root).EnrichmentMetadataPath("comix", "a"))!;
        Assert.Single(Directory.GetFiles(directory, "metadata.json"));
        Assert.Null(await store.ReadAsync("comix", "missing", CancellationToken.None));
    }

    [Fact]
    public async Task EnrichmentCorruptNewerAndOversizedAreReported()
    {
        var paths = new CatalogMirrorPaths(_root);
        var store = new CatalogEnrichmentStore(paths);

        var big = new CatalogTitleEnrichment(
            "comix", "big", "h-big", "Big", new string('x', 4_194_304), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<CatalogSnapshotException>(
            () => store.WriteAsync(big, CancellationToken.None));

        var target = paths.EnrichmentMetadataPath("comix", "c");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "{ not json");
        var before = await File.ReadAllBytesAsync(target);
        await Assert.ThrowsAsync<CatalogSnapshotException>(
            () => store.ReadAsync("comix", "c", CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(target));

        await File.WriteAllTextAsync(target, """{"schemaVersion":2,"enrichment":null}""");
        var exception = await Assert.ThrowsAsync<CatalogSnapshotException>(
            () => store.ReadAsync("comix", "c", CancellationToken.None));
        Assert.Contains("newer", exception.Message);

        await File.WriteAllBytesAsync(target, new byte[4_194_305]);
        await Assert.ThrowsAsync<CatalogSnapshotException>(
            () => store.ReadAsync("comix", "c", CancellationToken.None));
    }

    [Fact]
    public async Task CoverCacheFetchesOnceThenServesLocally()
    {
        var handler = new FakeHandler(_ => PngResponse());
        var cache = new CatalogMirrorCoverCache(
            new CatalogMirrorPaths(_root), new HttpClient(handler));

        var first = await cache.GetCoverPathAsync(
            "comix", "a", "https://comix.ws/covers/a.jpg", CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(File.Exists(first));

        var second = await cache.GetCoverPathAsync(
            "comix", "a", "https://comix.ws/covers/changed.jpg", CancellationToken.None);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(first, cache.TryGetCachedPath("comix", "a"));
    }

    [Fact]
    public async Task PoisonedCacheIsDroppedAndRefetched()
    {
        var paths = new CatalogMirrorPaths(_root);
        var handler = new FakeHandler(_ => PngResponse());
        var cache = new CatalogMirrorCoverCache(paths, new HttpClient(handler));

        var target = paths.EnrichmentCoverPath("comix", "p");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, [1, 2, 3, 4]);

        Assert.Null(cache.TryGetCachedPath("comix", "p"));
        Assert.False(File.Exists(target));

        var refetched = await cache.GetCoverPathAsync(
            "comix", "p", "https://comix.ws/covers/p.jpg", CancellationToken.None);
        Assert.NotNull(refetched);
        Assert.True(File.Exists(refetched));
    }

    [Fact]
    public async Task CoverCacheRefusesInvalidOversizedAndOffline()
    {
        var invalid = new CatalogMirrorCoverCache(
            new CatalogMirrorPaths(_root),
            new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            })));
        Assert.Null(await invalid.GetCoverPathAsync(
            "comix", "bad", "https://comix.ws/covers/bad.jpg", CancellationToken.None));

        var missing = new CatalogMirrorCoverCache(
            new CatalogMirrorPaths(_root),
            new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await missing.GetCoverPathAsync(
            "comix", "gone", "https://comix.ws/covers/gone.jpg", CancellationToken.None));

        var huge = new CatalogMirrorCoverCache(
            new CatalogMirrorPaths(_root),
            new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new SizedContent(new byte[10], CatalogMirrorCoverCache.MaxCoverBytes + 1),
            })));
        Assert.Null(await huge.GetCoverPathAsync(
            "comix", "huge", "https://comix.ws/covers/huge.jpg", CancellationToken.None));

        var calls = 0;
        var nonHttp = new CatalogMirrorCoverCache(
            new CatalogMirrorPaths(_root),
            new HttpClient(new FakeHandler(request => { calls++; return PngResponse(); })));
        Assert.Null(await nonHttp.GetCoverPathAsync(
            "comix", "local", "ftp://example/c.jpg", CancellationToken.None));
        Assert.Null(await nonHttp.GetCoverPathAsync("comix", "empty", null, CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.Null(nonHttp.TryGetCachedPath("comix", "local"));
    }

    private static HttpResponseMessage PngResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Convert.FromBase64String(TinyPngBase64)),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SizedContent(byte[] body, long declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    /// <summary>
    /// Seeds an isolated snapshot database: one building generation, paged
    /// appends, then activation. Blocking by design (xUnit has no
    /// synchronization context, so this cannot deadlock).
    /// </summary>
    private static CatalogMirrorQuery Seed(
        IEnumerable<CatalogSnapshotItem> items, int pageSize = 1000)
    {
        var paths = new CatalogMirrorPaths(
            Path.Combine(Path.GetTempPath(), "Citadel.CatalogMirror.Tests", Guid.NewGuid().ToString("N")));
        var store = new CatalogSnapshotStore(paths);
        var batch = new List<CatalogSnapshotItem>(pageSize);
        var page = 0;
        CatalogSnapshotCheckpoint? checkpoint = null;
        foreach (var item in items)
        {
            batch.Add(item);
            if (batch.Count < pageSize)
            {
                continue;
            }

            page++;
            checkpoint = store.AppendPageAsync(
                new CatalogSnapshotPartition("safe", "safe"), 0,
                new CatalogSnapshotPage(batch.ToArray(), null, page, true),
                CancellationToken.None).GetAwaiter().GetResult();
            batch = new List<CatalogSnapshotItem>(pageSize);
        }

        page++;
        checkpoint = store.AppendPageAsync(
            new CatalogSnapshotPartition("safe", "safe"), 0,
            new CatalogSnapshotPage(batch.ToArray(), null, page, false),
            CancellationToken.None).GetAwaiter().GetResult();

        var activated = store.ActivateAsync(checkpoint, CancellationToken.None).GetAwaiter().GetResult();
        return new CatalogMirrorQuery(new CatalogMirrorDatabase(paths), activated.SnapshotId);
    }

    private static CatalogMirrorQueryRequest Query(
        string search = "",
        CatalogMirrorFilter? filter = null,
        CatalogTitleSort sort = CatalogTitleSort.TitleAsc,
        int page = 1) =>
        new(search, filter ?? Filter(), sort, page);

    private static CatalogMirrorFilter Filter(
        CatalogContentRating[]? ratings = null,
        string[]? types = null,
        string[]? statuses = null,
        string[]? languages = null,
        int? yearFrom = null,
        int? yearTo = null,
        int? minLatestChapter = null) =>
        new(ratings ?? [], types ?? [], statuses ?? [], languages ?? [],
            yearFrom, yearTo, minLatestChapter);

    private static string[] Ids(CatalogMirrorResultPage page) =>
        page.Items.Select(item => item.TitleId).ToArray();

    private static CatalogSnapshotItem Item(
        string id,
        string? title = null,
        string rating = "safe",
        string type = "manga",
        string status = "releasing",
        string language = "en",
        int? year = 2020,
        long? latest = 1,
        string[]? alts = null) => new(
        SourceId: "comix",
        TitleId: id,
        TitleHid: "h-" + id,
        CanonicalUrl: "https://comix.ws/title/h-" + id,
        Title: title ?? "Title " + id,
        AlternateTitles: alts ?? [],
        CoverUrl: null,
        LatestChapterValue: latest,
        LatestChapterLabel: latest is null ? null : "Ch. " + latest,
        Rating: rating,
        Type: type,
        Status: status,
        Language: language,
        Year: year,
        Synopsis: null,
        CapturedAtUtc: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        IsTitlePlaceholder: false);
}
