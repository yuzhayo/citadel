using System.IO;
using System.Net;
using System.Net.Http;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Coordinator boundary: initialize/query/generation-guard, post-sync reload,
/// detail intent forwarding, and missing-source error, through a fake source
/// and temp stores. No WPF, no live provider, no real queue.
/// </summary>
public sealed class CatalogMirrorFeatureTests : IDisposable
{
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
    public async Task InitializeWithoutSnapshotStaysEmpty()
    {
        var feature = Coordinator();

        await feature.InitializeAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Empty, feature.Current.Sync.State);
        Assert.Null(feature.Current.Results);
        Assert.Equal(CatalogMirrorQueryRequest.Default, feature.Current.Query);
    }

    [Fact]
    public async Task InitializeLoadsActiveSnapshotLocally()
    {
        var paths = new CatalogMirrorPaths(_root);
        var store = new CatalogSnapshotStore(paths);
        var items = Enumerable.Range(0, 45).Select(number => SnapshotItem("t" + number)).ToArray();
        var checkpoint = await store.AppendPageAsync(
            new CatalogSnapshotPartition("safe", "safe"), 0,
            new CatalogSnapshotPage(items, 45, 1, false), CancellationToken.None);
        await store.ActivateAsync(checkpoint, CancellationToken.None);

        var feature = new CatalogMirrorFeature(new CatalogMirrorLoadFeature(store), null, Detail());
        await feature.InitializeAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Ready, feature.Current.Sync.State);
        Assert.Equal(45, feature.Current.Sync.UniqueTitles);
        Assert.Null(feature.Current.Results);

        Assert.True(await feature.LoadCatalogAsync(CancellationToken.None));
        Assert.NotNull(feature.Current.Results);
        Assert.Equal(45, feature.Current.Results.TotalResults);
        Assert.Equal(40, feature.Current.Results.Items.Count);
        Assert.Equal(["manga"], feature.AvailableTypes);
    }

    [Fact]
    public async Task LatestQueryWinsRegardlessOfFinishOrder()
    {
        var paths = new CatalogMirrorPaths(_root);
        var store = new CatalogSnapshotStore(paths);
        var items = Enumerable.Range(0, 3_000).Select(number => SnapshotItem("t" + number)).ToArray();
        var checkpoint = await store.AppendPageAsync(
            new CatalogSnapshotPartition("safe", "safe"), 0,
            new CatalogSnapshotPage(items, 3_000, 1, false), CancellationToken.None);
        await store.ActivateAsync(checkpoint, CancellationToken.None);

        var feature = new CatalogMirrorFeature(new CatalogMirrorLoadFeature(store), null, Detail());
        await feature.InitializeAsync(CancellationToken.None);
        await feature.LoadCatalogAsync(CancellationToken.None);

        var first = feature.QueryAsync(
            CatalogMirrorQueryRequest.Default with { Search = "t1" }, CancellationToken.None);
        var second = feature.QueryAsync(
            CatalogMirrorQueryRequest.Default with { Search = "t2999" }, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal("t2999", feature.Current.Query.Search);
        Assert.All(
            feature.Current.Results?.Items ?? [],
            item => Assert.Contains("t2999", item.Title, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncReadyReloadsIndexAndReappliesQuery()
    {
        var store = new CatalogSnapshotStore(new CatalogMirrorPaths(_root));
        var source = new FakeSnapshotSource((_, _) => Task.FromResult(
            new CatalogSnapshotPage(
                [SnapshotItem("n1", type: "manhwa"), SnapshotItem("n2", type: "manhwa")],
                2, 1, false)));
        var feature = new CatalogMirrorFeature(
            new CatalogMirrorLoadFeature(store), new CatalogMirrorSyncFeature(source, store), Detail());
        await feature.InitializeAsync(CancellationToken.None);
        Assert.Null(feature.Current.Results);

        await feature.StartSyncAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Ready, feature.Current.Sync.State);
        Assert.Null(feature.Current.Results);
        Assert.True(await feature.LoadCatalogAsync(CancellationToken.None));
        Assert.NotNull(feature.Current.Results);
        Assert.Equal(2, feature.Current.Results.TotalResults);
        Assert.Equal(["manhwa"], feature.AvailableTypes);
    }

    [Fact]
    public async Task MissingSourceReportsErrorWithoutThrowing()
    {
        var feature = new CatalogMirrorFeature(
            new CatalogMirrorLoadFeature(new CatalogSnapshotStore(new CatalogMirrorPaths(_root))), null, Detail());

        await feature.StartSyncAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Error, feature.Current.Sync.State);
        Assert.Contains("No snapshot source", feature.Current.Sync.ErrorMessage);
        feature.RequestStopSync();
    }

    [Fact]
    public async Task DetailIntentsFlowThrough()
    {
        var feature = new CatalogMirrorFeature(
            new CatalogMirrorLoadFeature(new CatalogSnapshotStore(new CatalogMirrorPaths(_root))), null, Detail());

        await feature.OpenTitleAsync(SnapshotItem("a"), CancellationToken.None);

        var detail = feature.Current.Detail;
        Assert.NotNull(detail);
        Assert.Equal("a", detail.Title.TitleId);
        Assert.False(detail.IsLoading);

        feature.SetChapterSelected("c1", true);
        var selected = feature.Current.Detail;
        Assert.NotNull(selected);
        Assert.True(selected.Chapters[0].IsSelected);

        feature.SelectAllChapters(false);
        var cleared = feature.Current.Detail;
        Assert.NotNull(cleared);
        Assert.All(
            cleared.Chapters,
            chapter => Assert.False(chapter.IsSelected));

        feature.CloseDetail();
        Assert.Null(feature.Current.Detail);
    }

    [Fact]
    public async Task SyncNeverLoadsResultsAndLoadCatalogReadsDatabaseExplicitly()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var source = new FakeSnapshotSource((partition, page) =>
        {
            calls++;
            if (calls == 2)
            {
                return gate.Task.ContinueWith(_ => new CatalogSnapshotPage(
                    [SnapshotItem("second")], 2, page, false),
                    CancellationToken.None);
            }

            return Task.FromResult(new CatalogSnapshotPage(
                [SnapshotItem("first")], null, page, calls == 1));
        });
        var store = new CatalogSnapshotStore(new CatalogMirrorPaths(_root));
        var sync = new CatalogMirrorSyncFeature(source, store);
        sync.PoliteDelay = TimeSpan.Zero;
        var feature = new CatalogMirrorFeature(new CatalogMirrorLoadFeature(store), sync, Detail());
        await feature.InitializeAsync(CancellationToken.None);
        Assert.Null(feature.Current.Results);

        var run = feature.StartSyncAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (feature.Current.Sync.StagedRecords == 0)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("First staged page never completed.");
            }

            await Task.Delay(25);
        }

        Assert.Null(feature.Current.Results);
        Assert.False(feature.Current.IsPartial);

        await feature.LoadCatalogAsync(CancellationToken.None);

        var partialResults = feature.Current.Results;
        Assert.NotNull(partialResults);
        Assert.True(feature.Current.IsPartial);
        Assert.Equal("first", partialResults.Items[0].TitleId);

        await feature.OpenTitleAsync(partialResults.Items[0], CancellationToken.None);
        Assert.Equal("first", feature.Current.Detail?.Title.TitleId);

        gate.SetResult(true);
        await run;

        Assert.Equal(CatalogSyncState.Ready, feature.Current.Sync.State);
        Assert.NotNull(feature.Current.Results);
        Assert.True(feature.Current.IsPartial);
        Assert.Equal(1, feature.Current.Results.TotalResults);
        Assert.Equal("first", feature.Current.Detail?.Title.TitleId);

        await feature.LoadCatalogAsync(CancellationToken.None);

        Assert.False(feature.Current.IsPartial);
        Assert.NotNull(feature.Current.Results);
        Assert.Equal(2, feature.Current.Results.TotalResults);
        Assert.Equal("first", feature.Current.Detail?.Title.TitleId);
    }

    private CatalogMirrorFeature Coordinator() =>
        new(
            new CatalogMirrorLoadFeature(new CatalogSnapshotStore(new CatalogMirrorPaths(_root))),
            null,
            Detail());

    private CatalogMirrorDetailFeature Detail() =>
        new(new FakeDirectory(),
            new CatalogEnrichmentStore(new CatalogMirrorPaths(_root)),
            new CatalogMirrorCoverCache(
                new CatalogMirrorPaths(_root),
                new HttpClient(new FakeHandler(_ => PngResponse()))),
            (_, _) => Task.FromResult(new CatalogLocalAvailabilityResult([])));

    private static HttpResponseMessage PngResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Convert.FromBase64String(TinyPngBase64)),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeDirectory : IMangaSourceDirectory
    {
        private readonly FakeDetailSource _source = new();

        public IReadOnlyList<IMangaSource> AvailableSources => [_source];

        public IMangaSource? FindSource(string? sourceId) => _source;
    }

    private sealed class FakeDetailSource : IMangaSource
    {
        public string Id => "comix";

        public string DisplayName => "Comix";

        public MangaSourceCapabilities Capabilities { get; } = new(true, true, [], false);

        public Task<RemoteCatalogPage> BrowseAsync(
            RemoteBrowseRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
            RemoteLookupKind kind, string query, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<RemoteTitleDetail> GetTitleAsync(
            RemoteTitleIdentity title, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteTitleDetail(
                new RemoteTitleSummary(title, "Detail " + title.TitleId, null, "Ch. 1"),
                null, [], []));

        public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
            RemoteTitleIdentity title, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteSourceGroup>>([
                new(new RemoteGroupIdentity("comix", "g1"), "Group One"),
            ]);

        public Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
            RemoteTitleIdentity title, RemoteGroupIdentity group, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteChapterSummary>>([
                new(new RemoteChapterIdentity("comix", title, "c1", "1", group), "Ch. 1", 0),
            ]);

        public Task<RemoteChapterManifest> GetManifestAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<RemotePageImage> TransformPageAsync(
            RemotePage page, byte[] payload, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class FakeSnapshotSource(
        Func<CatalogSnapshotPartition, int, Task<CatalogSnapshotPage>> fetch)
        : ICatalogSnapshotSource
    {
        public string SourceId => "fake";

        public IReadOnlyList<CatalogSnapshotPartition> SnapshotPartitions { get; } =
            [new("safe", "safe")];

        public Task<CatalogSnapshotPage> GetSnapshotPageAsync(
            CatalogSnapshotPartition partition,
            int page,
            CancellationToken cancellationToken,
            bool latestFirst = false) =>
            fetch(partition, page);
    }

    private static CatalogSnapshotItem SnapshotItem(string id, string type = "manga") => new(
        SourceId: "comix",
        TitleId: id,
        TitleHid: "h-" + id,
        CanonicalUrl: "https://comix.ws/title/h-" + id,
        Title: "Title " + id,
        AlternateTitles: [],
        CoverUrl: null,
        LatestChapterValue: 1,
        LatestChapterLabel: "Ch. 1",
        Rating: "safe",
        Type: type,
        Status: "releasing",
        Language: "en",
        Year: 2020,
        Synopsis: null,
        CapturedAtUtc: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        IsTitlePlaceholder: false);
}
