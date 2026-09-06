using Module.Mangareader.Features.Downloader.Catalog;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Chapter Selection ownership: the feature holds the authoritative selection the
/// queue receives, Select All reaches exactly the visible set the presentation
/// supplies, a group change clears it, and the stored value is an immutable
/// snapshot. The header checkbox's indeterminate rendering is a live gate — this
/// project deliberately does not link screen XAML.
/// </summary>
public sealed class ChapterSelectionTests
{
    private const string SourceId = "test";

    [Fact]
    public async Task SelectAllReachesExactlyTheVisibleSetThePresentationSupplies()
    {
        var feature = CreateFeature(out var source);
        feature.SelectSource(source.Id);
        await feature.StartAsync("query", CancellationToken.None);
        await feature.OpenTitleAsync(feature.State.Results[0], CancellationToken.None);

        var all = feature.State.Chapters.Select(chapter => chapter.Identity.ChapterId).ToList();
        feature.SetSelectedChapterIds(all);
        Assert.Equal(all, feature.State.SelectedChapterIds);
        Assert.True(feature.State.HasSelection);

        // A narrower visible set replaces the selection rather than adding to it,
        // so the queue can never receive a row the user cannot see.
        feature.SetSelectedChapterIds([all[1]]);
        Assert.Equal([all[1]], feature.State.SelectedChapterIds);

        feature.ClearSelection();
        Assert.Empty(feature.State.SelectedChapterIds);
        Assert.False(feature.State.HasSelection);
    }

    [Fact]
    public async Task ChangingSourceGroupClearsSelection()
    {
        var feature = CreateFeature(out var source);
        feature.SelectSource(source.Id);
        await feature.StartAsync("query", CancellationToken.None);
        await feature.OpenTitleAsync(feature.State.Results[0], CancellationToken.None);

        feature.SetSelectedChapterIds(
            feature.State.Chapters.Select(chapter => chapter.Identity.ChapterId).ToList());
        Assert.NotEmpty(feature.State.SelectedChapterIds);

        var identity = feature.State.Detail!.Summary.Identity;
        var otherGroup = new RemoteSourceGroup(new RemoteGroupIdentity(SourceId, "other"), "Other");
        await feature.SelectGroupAsync(identity, otherGroup, CancellationToken.None);

        // Variants are never merged, so a selection cannot survive a group change.
        Assert.Empty(feature.State.SelectedChapterIds);
        Assert.Equal(otherGroup.Identity, feature.State.SelectedGroup);
    }

    [Fact]
    public async Task AnUnchangedSelectionNeitherNotifiesNorRebuilds()
    {
        var feature = CreateFeature(out var source);
        feature.SelectSource(source.Id);
        await feature.StartAsync("query", CancellationToken.None);
        await feature.OpenTitleAsync(feature.State.Results[0], CancellationToken.None);

        var selected = feature.State.Chapters.Take(1).Select(chapter => chapter.Identity.ChapterId).ToList();
        feature.SetSelectedChapterIds(selected);

        var notifications = 0;
        feature.StateChanged += (_, _) => notifications++;

        // A checkbox click that lands on the same set must not start a render loop.
        feature.SetSelectedChapterIds(selected);
        Assert.Equal(0, notifications);

        feature.SetSelectedChapterIds([.. selected, "extra"]);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task TheStoredSelectionIsAnImmutableSnapshot()
    {
        var feature = CreateFeature(out var source);
        feature.SelectSource(source.Id);
        await feature.StartAsync("query", CancellationToken.None);
        await feature.OpenTitleAsync(feature.State.Results[0], CancellationToken.None);

        var mutable = feature.State.Chapters.Select(chapter => chapter.Identity.ChapterId).ToList();
        var expected = mutable.Count;
        feature.SetSelectedChapterIds(mutable);

        // The presentation reuses its list; the handed-off selection must not move.
        mutable.Clear();
        mutable.Add("tampered");

        Assert.Equal(expected, feature.State.SelectedChapterIds.Count);
        Assert.DoesNotContain("tampered", feature.State.SelectedChapterIds);
    }

    /// <summary>
    /// The re-download identity rule: the same number from another group is a
    /// distinct file, so re-downloading one variant can never overwrite another,
    /// while the same source and group resolves to one replaceable path.
    /// </summary>
    [Fact]
    public void ADifferentGroupPublishesADistinctFileName()
    {
        var chapter = Chapter("12", "c12");
        var official = new RemoteSourceGroup(new RemoteGroupIdentity(SourceId, "9897"), "Official");
        var scanlation = new RemoteSourceGroup(new RemoteGroupIdentity(SourceId, "77"), "Scanlation");

        var officialName = DownloadQueueFeature.BuildFileName(chapter, official);
        var scanlationName = DownloadQueueFeature.BuildFileName(chapter, scanlation);

        Assert.NotEqual(officialName, scanlationName);
        Assert.Contains("Official", officialName, StringComparison.Ordinal);
        Assert.Contains("Scanlation", scanlationName, StringComparison.Ordinal);

        // Same source and group is one stable target, so it replaces atomically.
        Assert.Equal(officialName, DownloadQueueFeature.BuildFileName(chapter, official));
    }

    private static CatalogFeature CreateFeature(out StubSource source)
    {
        source = new StubSource();
        var registry = new MangaSourceRegistry(
        [
            new MangaSourceRegistration(source, () => new NoFilters()),
        ]);
        return new CatalogFeature(registry, () => true);
    }

    private static RemoteChapterSummary Chapter(string number, string chapterId) =>
        new(
            new RemoteChapterIdentity(
                SourceId,
                new RemoteTitleIdentity(SourceId, "t1", "hid-t1", "slug-t1"),
                chapterId,
                number,
                new RemoteGroupIdentity(SourceId, "9897")),
            "Chapter " + number,
            0);

    private sealed class NoFilters : IRemoteFilterContribution, IRemoteFilterState
    {
        public System.Windows.FrameworkElement CreatePanel() => new System.Windows.Controls.Border();

        public IRemoteFilterState State => this;

        public IRemoteBrowseFilter? Snapshot(string? keyword) => null;

        public bool HasBlockingError => false;

        public string? ValidationMessage => null;
    }

    /// <summary>
    /// Only the calls chapter selection needs are answered; the rest throw so an
    /// unexpected provider interaction fails loudly instead of being absorbed.
    /// </summary>
    private sealed class StubSource : IMangaSource
    {
        public string Id => SourceId;

        public string DisplayName => "Stub source";

        public MangaSourceCapabilities Capabilities { get; } = new(true, false, [], false);

        public Task<RemoteCatalogPage> BrowseAsync(
            RemoteBrowseRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCatalogPage(
                [
                    new RemoteTitleSummary(
                        new RemoteTitleIdentity(SourceId, "t1", "hid-t1", "slug-t1"),
                        "Title 1",
                        null,
                        null),
                ],
                Total: 1,
                request.Page,
                HasMore: false));

        public Task<RemoteTitleDetail> GetTitleAsync(
            RemoteTitleIdentity title,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteTitleDetail(
                new RemoteTitleSummary(title, "Title 1", null, null),
                "description",
                [],
                []));

        public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
            RemoteTitleIdentity title,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteSourceGroup>>(
            [
                new RemoteSourceGroup(new RemoteGroupIdentity(SourceId, "9897"), "Official"),
            ]);

        public Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
            RemoteTitleIdentity title,
            RemoteGroupIdentity group,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteChapterSummary>>(
                string.Equals(group.GroupId, "9897", StringComparison.Ordinal)
                    ? [Chapter("1", "c1"), Chapter("2", "c2"), Chapter("10", "c10")]
                    : []);

        public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
            RemoteLookupKind kind, string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RemoteChapterManifest> GetManifestAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RemotePageImage> TransformPageAsync(
            RemotePage page, byte[] payload, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
