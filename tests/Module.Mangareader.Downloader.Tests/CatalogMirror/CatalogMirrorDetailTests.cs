using System.IO;
using System.Net;
using System.Net.Http;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Detail-flow boundary: explicit open/group/chapters/selection, the narrow
/// availability delegate, enrichment, a single cover, and the handoff builder,
/// through a fake directory, fake HTTP and fake delegates. No live browser,
/// no real Library or queue, no rendered controls.
/// </summary>
public sealed class CatalogMirrorDetailTests : IDisposable
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
    public async Task OpenTitleLoadsDetailGroupsFirstGroupAndSideEffects()
    {
        var source = new FakeSource();
        var availabilityCalls = new List<CatalogLocalAvailabilityRequest>();
        var detail = Detail(source, (request, _) =>
        {
            availabilityCalls.Add(request);
            return Task.FromResult(new CatalogLocalAvailabilityResult(
                request.Chapters.Select(chapter => new CatalogChapterAvailability(
                    chapter.ChapterId, chapter.ChapterId == "c1")).ToArray()));
        });

        await detail.OpenTitleAsync(Item("a"), CancellationToken.None);

        var state = detail.Current;
        Assert.NotNull(state);
        Assert.False(state.IsLoading);
        Assert.Null(state.ErrorMessage);
        Assert.Equal(["g1", "g2"], state.Groups.Select(group => group.GroupId).ToArray());
        Assert.Equal("g1", state.SelectedGroupId);
        Assert.Equal(["c1", "c2"], state.Chapters.Select(chapter => chapter.ChapterId).ToArray());
        Assert.All(state.Chapters, chapter => Assert.False(chapter.IsSelected));
        Assert.True(state.Chapters[0].IsAvailableLocally);
        Assert.False(state.Chapters[1].IsAvailableLocally);
        Assert.NotNull(state.Display);
        Assert.Equal("Detail a", state.Display.Title);
        Assert.NotNull(state.Display.CoverPath);
        Assert.True(File.Exists(state.Display.CoverPath));

        Assert.Equal(1, source.TitleCalls);
        Assert.Equal(1, source.GroupCalls);
        Assert.Equal([("a", "g1")], source.ChapterCalls);
        var availability = Assert.Single(availabilityCalls);
        Assert.Equal("a", availability.TitleId);
        Assert.Equal("g1", availability.GroupId);
        Assert.Equal(["c1", "c2"], availability.Chapters.Select(chapter => chapter.ChapterId).ToArray());

        var enrichment = await new CatalogEnrichmentStore(new CatalogMirrorPaths(_root))
            .ReadAsync("comix", "a", CancellationToken.None);
        Assert.NotNull(enrichment);
        Assert.Equal("Detail a", enrichment.DisplayName);
    }

    [Fact]
    public async Task OpenTitleFailureStaysLocal()
    {
        var source = new FakeSource { FailTitle = true };
        var detail = Detail(source);

        await detail.OpenTitleAsync(Item("a"), CancellationToken.None);

        var state = detail.Current;
        Assert.NotNull(state);
        Assert.False(state.IsLoading);
        Assert.NotNull(state.ErrorMessage);
        Assert.Empty(state.Groups);
        Assert.Empty(state.Chapters);
        Assert.Null(await new CatalogEnrichmentStore(new CatalogMirrorPaths(_root))
            .ReadAsync("comix", "a", CancellationToken.None));
    }

    [Fact]
    public async Task SelectGroupClearsSelectionAndReloads()
    {
        var source = new FakeSource();
        var groups = new List<string>();
        var detail = Detail(source, (request, _) =>
        {
            groups.Add(request.GroupId);
            return Task.FromResult(new CatalogLocalAvailabilityResult([]));
        });

        await detail.OpenTitleAsync(Item("a"), CancellationToken.None);
        detail.SetChapterSelected("c1", true);
        var selected = detail.Current;
        Assert.NotNull(selected);
        Assert.True(selected.Chapters[0].IsSelected);

        await detail.SelectGroupAsync("g2", CancellationToken.None);

        var state = detail.Current;
        Assert.NotNull(state);
        Assert.Equal("g2", state.SelectedGroupId);
        Assert.Equal(["c3"], state.Chapters.Select(chapter => chapter.ChapterId).ToArray());
        Assert.All(state.Chapters, chapter => Assert.False(chapter.IsSelected));
        Assert.Equal(["g1", "g2"], groups);
    }

    [Fact]
    public async Task SelectionBlockedWhileLoading()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource { ChapterGate = gate.Task };
        var detail = Detail(source);

        var open = detail.OpenTitleAsync(Item("a"), CancellationToken.None);
        await WaitForAsync(() => source.ChapterCalls.Count >= 1);
        detail.SetChapterSelected("c1", true);
        detail.SelectAllChapters(true);
        gate.SetResult(true);
        await open;

        // Selection attempts during loading never stick.
        var state = detail.Current;
        Assert.NotNull(state);
        Assert.All(state.Chapters, chapter => Assert.False(chapter.IsSelected));
    }

    [Fact]
    public async Task StaleOpenNeverReplacesNewerTitle()
    {
        var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource();
        var calls = 0;
        source.TitleHook = async call =>
        {
            calls = call;
            if (call == 1)
            {
                await firstGate.Task;
            }
        };
        var detail = Detail(source);

        var first = detail.OpenTitleAsync(Item("a"), CancellationToken.None);
        await WaitForAsync(() => calls >= 1);
        await detail.OpenTitleAsync(Item("b"), CancellationToken.None);
        firstGate.SetResult(true);
        await first;

        var state = detail.Current;
        Assert.NotNull(state);
        Assert.Equal("b", state.Title.TitleId);
    }

    [Fact]
    public async Task HandoffRequiresSelectionAndFolder()
    {
        var source = new FakeSource();
        var detail = Detail(source);
        await detail.OpenTitleAsync(Item("a"), CancellationToken.None);

        Assert.Null(detail.TryBuildHandoff("/lib/Folder"));
        Assert.Null(detail.TryBuildHandoff("   "));

        detail.SetChapterSelected("c2", true);
        var handoff = detail.TryBuildHandoff("/lib/Folder");
        Assert.NotNull(handoff);

        Assert.Equal("comix", handoff.SourceId);
        Assert.Equal("a", handoff.TitleId);
        Assert.Equal("Detail a", handoff.TitleDisplayName);
        Assert.Equal("g1", handoff.GroupId);
        Assert.Equal("Group One", handoff.GroupDisplayName);
        var chapter = Assert.Single(handoff.Chapters);
        Assert.Equal("c2", chapter.ChapterId);
        Assert.Equal("2", chapter.ChapterNumber);
        Assert.Equal("/lib/Folder", handoff.TargetFolder);
    }

    [Fact]
    public async Task CoverAndAvailabilityFailuresKeepDetail()
    {
        var source = new FakeSource();
        var missing = Detail(source, status: HttpStatusCode.NotFound);
        await missing.OpenTitleAsync(Item("a"), CancellationToken.None);

        var withoutCover = missing.Current;
        Assert.NotNull(withoutCover);
        Assert.False(withoutCover.IsLoading);
        Assert.Null(withoutCover.ErrorMessage);
        Assert.NotNull(withoutCover.Display);
        Assert.Null(withoutCover.Display.CoverPath);

        var failing = Detail(source, (_, _) =>
            Task.FromException<CatalogLocalAvailabilityResult>(
                new InvalidOperationException("probe down")));
        await failing.OpenTitleAsync(Item("a"), CancellationToken.None);

        var withoutVerdicts = failing.Current;
        Assert.NotNull(withoutVerdicts);
        Assert.Null(withoutVerdicts.ErrorMessage);
        Assert.All(withoutVerdicts.Chapters, chapter => Assert.False(chapter.IsAvailableLocally));
    }

    [Fact]
    public void NothingRunsBeforeOpen()
    {
        var source = new FakeSource();
        _ = Detail(source);

        Assert.Equal(0, source.TitleCalls);
        Assert.Equal(0, source.GroupCalls);
        Assert.Empty(source.ChapterCalls);
        Assert.Null(new CatalogMirrorDetailFeature(
            new FakeDirectory(source),
            new CatalogEnrichmentStore(new CatalogMirrorPaths(_root)),
            new CatalogMirrorCoverCache(
                new CatalogMirrorPaths(_root),
                new HttpClient(new FakeHandler(_ => PngResponse()))),
            (_, _) => Task.FromResult(new CatalogLocalAvailabilityResult([]))).Current);
    }

    private CatalogMirrorDetailFeature Detail(
        FakeSource source,
        Func<CatalogLocalAvailabilityRequest, CancellationToken, Task<CatalogLocalAvailabilityResult>>? availability = null,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(new FakeDirectory(source),
            new CatalogEnrichmentStore(new CatalogMirrorPaths(_root)),
            new CatalogMirrorCoverCache(
                new CatalogMirrorPaths(_root), new HttpClient(new FakeHandler(_ => StatusResponse(status)))),
            availability ?? ((_, _) => Task.FromResult(new CatalogLocalAvailabilityResult([]))));

    private static async Task WaitForAsync(Func<bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the detail flow.");
            }

            await Task.Delay(25);
        }
    }

    private static HttpResponseMessage StatusResponse(HttpStatusCode status) =>
        status == HttpStatusCode.OK
            ? PngResponse()
            : new HttpResponseMessage(status);

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

    private sealed class FakeDirectory(FakeSource source) : IMangaSourceDirectory
    {
        public IReadOnlyList<IMangaSource> AvailableSources => [source];

        public IMangaSource? FindSource(string? sourceId) =>
            string.Equals(sourceId, "comix", StringComparison.Ordinal) ? source : null;
    }

    private sealed class FakeSource : IMangaSource
    {
        public int TitleCalls;
        public int GroupCalls;
        public readonly List<(string Title, string Group)> ChapterCalls = [];
        public bool FailTitle;
        public Task? ChapterGate;
        public Func<int, Task>? TitleHook;

        public string Id => "comix";

        public string DisplayName => "Comix";

        public MangaSourceCapabilities Capabilities { get; } = new(true, true, [], false);

        public Task<RemoteCatalogPage> BrowseAsync(
            RemoteBrowseRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
            RemoteLookupKind kind, string query, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public async Task<RemoteTitleDetail> GetTitleAsync(
            RemoteTitleIdentity title, CancellationToken cancellationToken)
        {
            TitleCalls++;
            if (TitleHook is not null)
            {
                await TitleHook(TitleCalls);
            }

            if (FailTitle)
            {
                throw new InvalidOperationException("detail down");
            }

            return new RemoteTitleDetail(
                new RemoteTitleSummary(
                    title, "Detail " + title.TitleId,
                    "https://comix.ws/covers/x.jpg", "Ch. 3"),
                "A synopsis.",
                [new RemoteOption("1", "Action")],
                [new RemoteOption("Official", "Group")]);
        }

        public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
            RemoteTitleIdentity title, CancellationToken cancellationToken)
        {
            GroupCalls++;
            return Task.FromResult<IReadOnlyList<RemoteSourceGroup>>([
                new(new RemoteGroupIdentity("comix", "g1"), "Group One"),
                new(new RemoteGroupIdentity("comix", "g2"), "Group Two"),
            ]);
        }

        public async Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
            RemoteTitleIdentity title, RemoteGroupIdentity group, CancellationToken cancellationToken)
        {
            ChapterCalls.Add((title.TitleId, group.GroupId));
            if (ChapterGate is not null)
            {
                await ChapterGate;
            }

            return group.GroupId == "g1"
                ? [Chapter(title, group, "c1", "1"), Chapter(title, group, "c2", "2")]
                : [Chapter(title, group, "c3", "3")];
        }

        public Task<RemoteChapterManifest> GetManifestAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<RemotePageImage> TransformPageAsync(
            RemotePage page, byte[] payload, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        private static RemoteChapterSummary Chapter(
            RemoteTitleIdentity title, RemoteGroupIdentity group, string id, string number) =>
            new(new RemoteChapterIdentity("comix", title, id, number, group), "Ch. " + number, 0);
    }

    private static CatalogSnapshotItem Item(string id) => new(
        SourceId: "comix",
        TitleId: id,
        TitleHid: "h-" + id,
        CanonicalUrl: "https://comix.ws/title/h-" + id,
        Title: "Title " + id,
        AlternateTitles: [],
        CoverUrl: null,
        LatestChapterValue: 3,
        LatestChapterLabel: "Ch. 3",
        Rating: "safe",
        Type: "manga",
        Status: "releasing",
        Language: "en",
        Year: 2020,
        Synopsis: null,
        CapturedAtUtc: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        IsTitlePlaceholder: false);
}
