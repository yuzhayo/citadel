using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.Features.Downloader.Catalog;
using Module.Mangareader.Sources;
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

    /// <summary>
    /// A logical Stop must leave the transport alive. Cancelling the caller's token
    /// would not stop the Python work: the inline pyhost command links that token to
    /// its own bounded timeout, so a cancel completes the pending call at once with a
    /// TIMEOUT while Python is still busy — a fake provider error, a bar that returns
    /// to Start too early, and a following Start written before the old command
    /// settled. The request is marked stale instead, and its late result still
    /// cannot commit.
    ///
    /// The screen's own Stop button and its parked <c>Stopping…</c> state are a live
    /// gate; this project does not link screen XAML.
    /// </summary>
    [Fact]
    public async Task ALogicalStopLeavesTheTransportAliveAndCannotCommitItsLateResponse()
    {
        var source = new CountingSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The token the provider actually sees, so a cancel cannot hide behind the
        // caller's own copy.
        var providerToken = CancellationToken.None;
        source.BrowseBehavior = (_, token) =>
        {
            providerToken = token;
            return gate.Task.ContinueWith(_ => Page("late"), TaskScheduler.Default);
        };

        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);

        // A real cancellable transport, so "not cancelled" is a meaningful claim
        // rather than a tautology about CancellationToken.None.
        using var transport = new CancellationTokenSource();
        var pending = feature.StartAsync("query", transport.Token);
        Assert.True(feature.State.IsBusy);
        Assert.True(providerToken.CanBeCanceled);

        feature.AbandonActiveBrowse();

        Assert.False(transport.IsCancellationRequested);
        Assert.False(providerToken.IsCancellationRequested);
        Assert.False(feature.State.IsBusy);
        Assert.Empty(feature.State.Results);
        Assert.Null(feature.State.ErrorMessage);

        // The bounded command now settles on its own terms, not because it was cut
        // short, and its late result is still refused.
        gate.SetResult();
        await pending;

        Assert.Empty(feature.State.Results);
        Assert.Null(feature.State.TotalCount);
        Assert.False(feature.State.IsBusy);
        Assert.Null(feature.State.ErrorMessage);
        Assert.Equal(1, source.BrowseCalls);
        Assert.False(transport.IsCancellationRequested);
        Assert.False(providerToken.IsCancellationRequested);
    }

    /// <summary>
    /// A Stop belongs to the Browse and to nothing else. While one staleness counter
    /// was shared by every operation, abandoning a Browse also discarded an unrelated
    /// title detail that was still in flight — the user's Stop silently cancelled the
    /// detail they had just opened. The abandoned Browse's late result is still
    /// refused, and it may not commit over the detail either.
    /// </summary>
    [Fact]
    public async Task AbandoningABrowseKeepsAnInFlightDetailAliveAndStillRefusesTheLateBrowse()
    {
        var source = new CountingSource();
        var browseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BrowseBehavior = (_, _) =>
            browseGate.Task.ContinueWith(_ => Page("late"), TaskScheduler.Default);
        source.TitleBehavior = (_, _) =>
            detailGate.Task.ContinueWith(
                _ => new RemoteTitleDetail(Summary("t1"), "description", [], []),
                TaskScheduler.Default);

        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);

        var browse = feature.StartAsync("query", CancellationToken.None);
        var detail = feature.OpenTitleAsync(Summary("t1"), CancellationToken.None);
        Assert.True(feature.State.IsBusy);

        feature.AbandonActiveBrowse();

        // Stop ends the Browse half only. The union the presentation disables input on
        // stays true while the detail is still being fetched, which is what makes
        // "Stop only stops the Browse" true for the user and not just for the result
        // set: the provider picker and the search field must not come back mid-action.
        Assert.False(feature.State.IsBrowseBusy);
        Assert.True(feature.State.IsActionBusy);
        Assert.True(feature.State.IsBusy);

        // The detail was already in flight when the Browse was abandoned, so it
        // keeps its own lifecycle and commits.
        detailGate.SetResult();
        await detail;

        Assert.True(feature.State.IsDetailOpen);
        Assert.NotNull(feature.State.Detail);
        Assert.Single(feature.State.Groups);
        Assert.Single(feature.State.Chapters);
        Assert.False(feature.State.IsBusy);

        // The abandoned Browse settles afterwards and is still refused: it cannot
        // replace the result set and cannot close the detail that won.
        browseGate.SetResult();
        await browse;

        Assert.Empty(feature.State.Results);
        Assert.Null(feature.State.TotalCount);
        Assert.Null(feature.State.ErrorMessage);
        Assert.True(feature.State.IsDetailOpen);
        Assert.NotNull(feature.State.Detail);
        Assert.Equal(1, source.BrowseCalls);
    }

    /// <summary>
    /// The other direction of the same rule. A Browse started after a detail is the
    /// newer navigation, so the older detail may not re-open over the fresh result
    /// page. Because an abandoned action returns early without touching state, the
    /// Browse also has to release its busy half — otherwise the screen would report
    /// work in flight forever and its input would never come back.
    /// </summary>
    [Fact]
    public async Task ANewBrowseWinsOverAnOlderDetailThatIsStillInFlight()
    {
        var source = new CountingSource();
        var detailGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TitleBehavior = (_, _) =>
            detailGate.Task.ContinueWith(
                _ => new RemoteTitleDetail(Summary("old"), "description", [], []),
                TaskScheduler.Default);

        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);

        var detail = feature.OpenTitleAsync(Summary("old"), CancellationToken.None);
        Assert.True(feature.State.IsActionBusy);

        await feature.StartAsync("query", CancellationToken.None);

        Assert.Equal(["t1"], Ids(feature));
        Assert.False(feature.State.IsBusy);

        detailGate.SetResult();
        await detail;

        // The older detail is refused: it cannot re-open over the newer result page,
        // and it cannot put the screen back into a busy state that nothing will clear.
        Assert.False(feature.State.IsDetailOpen);
        Assert.Null(feature.State.Detail);
        Assert.Equal(["t1"], Ids(feature));
        Assert.False(feature.State.IsBusy);
        Assert.Null(feature.State.ErrorMessage);
    }

    /// <summary>
    /// Leaving the detail is a navigation too, and it is reachable: change group, then
    /// press Back before the chapters land. The abandoned chapter load must not
    /// repopulate a detail that is already closed, and Back has to release the action's
    /// busy half because that load now returns early without touching state.
    /// </summary>
    [Fact]
    public async Task LeavingTheDetailWinsOverAChapterLoadThatIsStillInFlight()
    {
        var source = new CountingSource();
        var feature = CreateFeature(source, blockingFilter: false);
        feature.SelectSource(source.Id);
        await feature.StartAsync("query", CancellationToken.None);
        await feature.OpenTitleAsync(feature.State.Results[0], CancellationToken.None);
        Assert.Single(feature.State.Chapters);

        var chaptersGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ChaptersBehavior = (_, _, _) =>
            chaptersGate.Task.ContinueWith(_ => LateChapters(), TaskScheduler.Default);

        var groupChange = feature.SelectGroupAsync(
            feature.State.Detail!.Summary.Identity,
            feature.State.Groups[0],
            CancellationToken.None);
        Assert.True(feature.State.IsActionBusy);

        feature.Back();

        Assert.False(feature.State.IsDetailOpen);
        Assert.False(feature.State.IsBusy);

        chaptersGate.SetResult();
        await groupChange;

        // The closed detail stays closed and empty, and the screen is not left
        // reporting a chapter load that will never commit.
        Assert.False(feature.State.IsDetailOpen);
        Assert.Null(feature.State.Detail);
        Assert.Empty(feature.State.Chapters);
        Assert.False(feature.State.IsBusy);
        Assert.Null(feature.State.ErrorMessage);
    }

    private static IReadOnlyList<RemoteChapterSummary> LateChapters() =>
    [
        new RemoteChapterSummary(
            new RemoteChapterIdentity(
                "test",
                Summary("t1").Identity,
                "late",
                "9",
                new RemoteGroupIdentity("test", "9897")),
            "Chapter 9",
            0),
    ];

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

        public IRemoteBrowseFilter? Snapshot(string? keyword) => new FakeFilter();

        public bool HasBlockingError => blocking;

        public string? ValidationMessage => blocking ? "invalid range" : null;
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

        /// <summary>
        /// Optional hook so a detail action can be held in flight the same way a
        /// Browse can. Null keeps the immediate response every other test relies on.
        /// </summary>
        public Func<RemoteTitleIdentity, CancellationToken, Task<RemoteTitleDetail>>? TitleBehavior;

        /// <summary>Optional hook so a chapter load can be held in flight. Null keeps the immediate list.</summary>
        public Func<RemoteTitleIdentity, RemoteGroupIdentity, CancellationToken, Task<IReadOnlyList<RemoteChapterSummary>>>? ChaptersBehavior;

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
            TitleBehavior?.Invoke(title, cancellationToken)
            ?? Task.FromResult(new RemoteTitleDetail(
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
            ChaptersBehavior?.Invoke(title, group, cancellationToken)
            ?? Task.FromResult<IReadOnlyList<RemoteChapterSummary>>(
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
