using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.Features.Downloader.Catalog;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The explicit-trigger and latest-request-wins gates. Catalog must not touch
/// the provider on open, provider selection or filter edits; an invalid range
/// must never reach the provider; and an older response must never commit over
/// a newer request.
/// </summary>
public sealed class CatalogNetworkTriggerTests
{
    [Fact]
    public async Task SelectingAProviderIssuesNoRequest()
    {
        var source = new CountingSource();
        var feature = CreateFeature(source, blockingFilter: false);

        feature.SelectSource(source.Id);

        Assert.Equal(0, source.BrowseCalls);
        Assert.Equal(source.Id, feature.State.SelectedSourceId);
        Assert.NotNull(feature.Filters);
    }

    [Fact]
    public async Task InvalidFilterRangeNeverReachesTheProvider()
    {
        var source = new CountingSource();
        var feature = CreateFeature(source, blockingFilter: true);
        feature.SelectSource(source.Id);

        await feature.StartAsync(null, CancellationToken.None);

        Assert.Equal(0, source.BrowseCalls);
        Assert.Equal("invalid range", feature.State.ErrorMessage);
        Assert.False(feature.State.IsBusy);
    }

    [Fact]
    public async Task StartIssuesExactlyOneRequestAndReplacesTheResultSet()
    {
        var source = new CountingSource();
        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);

        await feature.StartAsync("first", CancellationToken.None);

        Assert.Equal(1, source.BrowseCalls);
        Assert.Equal(["t1"], Ids(feature));

        source.PagesByNumber[1] = [Summary("t2")];
        await feature.StartAsync("second", CancellationToken.None);

        Assert.Equal(2, source.BrowseCalls);
        Assert.Equal(["t2"], Ids(feature));
    }

    [Fact]
    public async Task AnOlderResponseCannotCommitAfterANewerRequest()
    {
        var source = new CountingSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BrowseBehavior = (request, _) =>
            request.Query == "slow"
                ? gate.Task.ContinueWith(_ => Page("stale"), TaskScheduler.Default)
                : Task.FromResult(Page("current"));

        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);

        var slow = feature.StartAsync("slow", CancellationToken.None);
        var fast = feature.StartAsync("fast", CancellationToken.None);
        await fast;

        Assert.Equal(["current"], Ids(feature));

        // The stale response is released and finishes its cleanup, but it cannot
        // replace results, count, error or pagination.
        gate.SetResult();
        await slow;

        Assert.Equal(["current"], Ids(feature));
        Assert.Equal(2, source.BrowseCalls);
        Assert.Null(feature.State.ErrorMessage);
    }

    [Fact]
    public async Task LoadMoreAppendsTheNextPageOfTheSameQuerySnapshot()
    {
        var source = new CountingSource();
        source.PagesByNumber[2] = [Summary("t2")];
        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);

        await feature.StartAsync("query", CancellationToken.None);
        Assert.True(feature.State.CanLoadMore);

        await feature.LoadMoreAsync(CancellationToken.None);

        Assert.Equal(2, source.BrowseCalls);
        Assert.Equal(2, source.LastRequest?.Page);
        Assert.Equal("query", source.LastRequest?.Query);
        Assert.Equal(["t1", "t2"], Ids(feature));
        Assert.False(feature.State.CanLoadMore);
    }

    [Fact]
    public async Task BackRestoresTheGridWithoutANewRequest()
    {
        var source = new CountingSource();
        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);
        await feature.StartAsync("query", CancellationToken.None);
        var callsAfterStart = source.BrowseCalls;

        feature.RememberScrollOffset(123.5);
        await feature.OpenTitleAsync(feature.State.Results[0], CancellationToken.None);

        Assert.True(feature.State.IsDetailOpen);
        Assert.NotNull(feature.State.Detail);
        Assert.Single(feature.State.Groups);
        Assert.Single(feature.State.Chapters);

        feature.Back();

        Assert.False(feature.State.IsDetailOpen);
        Assert.Null(feature.State.Detail);
        Assert.Equal(123.5, feature.State.ResultsScrollOffset);
        Assert.Equal(["t1"], Ids(feature));

        // Detail discovery is a trigger; returning to the grid is not.
        Assert.Equal(callsAfterStart, source.BrowseCalls);
    }

    [Fact]
    public async Task AFailedRequestLeavesTheLastSuccessfulResultVisible()
    {
        var source = new CountingSource();
        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);
        await feature.StartAsync("query", CancellationToken.None);

        source.BrowseBehavior = (_, _) =>
            Task.FromException<RemoteCatalogPage>(new InvalidOperationException("provider down"));
        await feature.StartAsync("query", CancellationToken.None);

        Assert.Equal("provider down", feature.State.ErrorMessage);
        Assert.Equal(["t1"], Ids(feature));
        Assert.False(feature.State.IsBusy);
    }

    private static IReadOnlyList<string> Ids(CatalogFeature feature) =>
        [.. feature.State.Results.Select(item => item.Identity.TitleId)];

    private static CatalogFeature CreateFeature(CountingSource source, bool blockingFilter)
    {
        var registry = new MangaSourceRegistry(
        [
            new MangaSourceRegistration(source, () => new FakeFilters(blockingFilter)),
        ]);
        return new CatalogFeature(registry, () => true);
    }

    private static RemoteCatalogPage Page(string titleId) =>
        new([Summary(titleId)], Total: 1, Page: 1, HasMore: false);

    private static RemoteTitleSummary Summary(string titleId) =>
        new(
            new RemoteTitleIdentity("test", titleId, "hid-" + titleId, "slug-" + titleId),
            "Title " + titleId,
            null,
            "Chapter 1");

    private sealed record FakeFilter : IRemoteBrowseFilter
    {
        public string SourceId => "test";
    }

    private sealed class FakeFilters(bool blocking) : IRemoteFilterContribution, IRemoteFilterState
    {
        public FrameworkElement CreatePanel() => new Border();

        public IRemoteFilterState State => this;

        public IRemoteBrowseFilter? CurrentFilter => new FakeFilter();

        public bool HasBlockingError => blocking;

        public string? ValidationMessage => blocking ? "invalid range" : null;

        public void Reset()
        {
        }
    }

    /// <summary>
    /// A controlled provider double: the network is the expensive,
    /// uncontrollable boundary, so it is replaced while every real Catalog
    /// decision under test still runs.
    /// </summary>
    private sealed class CountingSource : IMangaSource
    {
        public int BrowseCalls;

        public Dictionary<int, IReadOnlyList<RemoteTitleSummary>> PagesByNumber = new()
        {
            [1] = [Summary("t1")],
        };

        public Func<RemoteBrowseRequest, CancellationToken, Task<RemoteCatalogPage>> BrowseBehavior;

        public RemoteBrowseRequest? LastRequest { get; private set; }

        public CountingSource() =>
            BrowseBehavior = (request, _) =>
            {
                var items = PagesByNumber.TryGetValue(request.Page, out var page)
                    ? page
                    : [];
                return Task.FromResult(new RemoteCatalogPage(
                    items,
                    Total: 2,
                    request.Page,
                    HasMore: PagesByNumber.ContainsKey(request.Page + 1)));
            };

        public string Id => "test";

        public string DisplayName => "Test source";

        public MangaSourceCapabilities Capabilities { get; } =
            new(true, true, [RemoteLookupKind.Author], TransformsPages: false);

        public Task<RemoteCatalogPage> BrowseAsync(
            RemoteBrowseRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref BrowseCalls);
            LastRequest = request;
            return BrowseBehavior(request, cancellationToken);
        }

        public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
            RemoteLookupKind kind,
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteLookupOption>>(
                [new RemoteLookupOption("k-" + query, query)]);

        public Task<RemoteTitleDetail> GetTitleAsync(
            RemoteTitleIdentity title,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteTitleDetail(
                Summary(title.TitleId),
                "description",
                [new RemoteOption("action", "Action")],
                []));

        public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
            RemoteTitleIdentity title,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteSourceGroup>>(
                [new RemoteSourceGroup(new RemoteGroupIdentity(Id, "9897"), "Official")]);

        public Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
            RemoteTitleIdentity title,
            RemoteGroupIdentity group,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteChapterSummary>>(
            [
                new RemoteChapterSummary(
                    new RemoteChapterIdentity(Id, title, "c1", "1", group),
                    "Chapter 1",
                    0),
            ]);

        public Task<RemoteChapterManifest> GetManifestAsync(
            RemoteChapterIdentity chapter,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteChapterManifest(
                chapter,
                [new RemotePage(0, "0", "https://example.invalid/0.png", null, null)],
                "sha256:test",
                new Dictionary<string, string>()));

        public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
            RemoteChapterIdentity chapter,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteAlternateChapter>>([]);

        public Task<RemotePageImage> TransformPageAsync(
            RemotePage page,
            byte[] payload,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RemotePageImage(payload, "png"));
    }
}
