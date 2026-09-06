using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Module.Mangareader.Sources;
using Module.Mangareader.Library.UpdateChecker;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library.Tests;

/// <summary>
/// The locked matching order and the durable binding: an embedded Citadel
/// identity needs no URL input, a confirmed mapping is used before any search,
/// a search binds only when exactly one candidate is safe, and ambiguity is
/// always handed back for confirmation instead of being resolved silently.
/// </summary>
public sealed class UpdateMatcherTests : IDisposable
{
    private const string ProviderId = "fake";
    private const string Folder = "Local Title";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-updatechecker-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _library;
    private readonly string _bindingsPath;
    private readonly FakeSource _source = new();

    public UpdateMatcherTests()
    {
        _library = Path.Combine(_root, "library");
        _bindingsPath = Path.Combine(_root, "update-bindings.json");
        Directory.CreateDirectory(Path.Combine(_library, Folder));
    }

    private UpdateMatcher Matcher(Func<string, ConfirmedTitleMapping?>? mappings = null) =>
        new(new LocalTitleProbe(), new FakeDirectory(_source), mappings ?? (_ => null));

    private UpdateCheckerFeature Feature(
        Func<string, ConfirmedTitleMapping?>? mappings = null,
        EnqueueUpdateChapters? enqueue = null)
    {
        var store = new SourceBindingStore(_bindingsPath);
        return new UpdateCheckerFeature(
            store,
            Matcher(mappings),
            new LocalTitleProbe(),
            new FakeDirectory(_source),
            () => _library,
            enqueue);
    }

    [Fact]
    public async Task AnEmbeddedIdentityBindsWithoutAnyUrlInput()
    {
        WriteCbz("Chapter 1.cbz", chapterId: "c1", number: "1", groupId: "g1", groupName: "Official");

        // No confirmed mapping exists, so the hid the adapter needs is resolved
        // from one bounded provider search that returns the same title id.
        _source.BrowseResults = [Title("12947", "dy88", "Local Title")];

        var result = await Matcher().MatchAsync(_library, Folder, CancellationToken.None);

        Assert.Equal(UpdateMatchKind.Unique, result.Kind);
        Assert.NotNull(result.Match);
        Assert.Equal("12947", result.Match!.Title.TitleId);
        Assert.Equal("dy88", result.Match.Title.TitleHid);
        Assert.Equal("g1", result.Match.Group.GroupId);
        Assert.Equal("Official", result.Match.GroupDisplayName);
        Assert.Equal(1, _source.BrowseCalls);
    }

    [Fact]
    public async Task AConfirmedIndexMappingIsUsedBeforeAnySearch()
    {
        WriteCbz("Chapter 1.cbz", chapterId: null, number: null, groupId: null, groupName: null);
        WriteCbz("Chapter 2.cbz", chapterId: null, number: null, groupId: null, groupName: null);

        _source.Groups["dy88"] = [Group("g1", "Official")];
        _source.Chapters["dy88|g1"] = Chapters("dy88", "g1", "1", "2", "3");

        var result = await Matcher(Mapping("12947", "dy88"))
            .MatchAsync(_library, Folder, CancellationToken.None);

        Assert.Equal(UpdateMatchKind.Unique, result.Kind);
        Assert.Equal(2, result.Match!.OverlappingChapters);

        // The mapping is step 2: it must not fall through to a provider search.
        Assert.Equal(0, _source.BrowseCalls);
    }

    [Fact]
    public async Task AUniqueSearchMatchIsBound()
    {
        WriteCbz("Chapter 1.cbz", chapterId: null, number: null, groupId: null, groupName: null);
        WriteCbz("Chapter 2.cbz", chapterId: null, number: null, groupId: null, groupName: null);

        _source.BrowseResults = [Title("777", "ab12", "Local Title")];
        _source.Groups["ab12"] = [Group("g9", "Scanlation")];
        _source.Chapters["ab12|g9"] = Chapters("ab12", "g9", "1", "2", "3", "4");

        var result = await Matcher().MatchAsync(_library, Folder, CancellationToken.None);

        Assert.Equal(UpdateMatchKind.Unique, result.Kind);
        Assert.Equal("777", result.Match!.Title.TitleId);
        Assert.Equal("g9", result.Match.Group.GroupId);
        Assert.Equal(2, result.Match.OverlappingChapters);
    }

    [Fact]
    public async Task AnAmbiguousSearchStaysUnboundUntilConfirmed()
    {
        WriteCbz("Chapter 1.cbz", chapterId: null, number: null, groupId: null, groupName: null);
        WriteCbz("Chapter 2.cbz", chapterId: null, number: null, groupId: null, groupName: null);

        _source.BrowseResults =
        [
            Title("1", "hid-one", "Local Title"),
            Title("2", "hid-two", "Local Title"),
        ];
        _source.Groups["hid-one"] = [Group("g1", "Group one")];
        _source.Groups["hid-two"] = [Group("g2", "Group two")];
        _source.Chapters["hid-one|g1"] = Chapters("hid-one", "g1", "1", "2");
        _source.Chapters["hid-two|g2"] = Chapters("hid-two", "g2", "1", "2");

        var feature = Feature();
        await feature.CheckAsync(Folder, CancellationToken.None);

        Assert.Equal(UpdateCheckState.NeedsChoice, feature.State);
        Assert.Equal(2, feature.Candidates.Count);
        Assert.Null(feature.Binding);
        Assert.Empty(feature.MissingChapters);

        // Nothing was persisted: an ambiguous match never binds itself.
        Assert.False(File.Exists(_bindingsPath));

        // One confirmation binds exactly the chosen candidate and continues.
        await feature.ConfirmCandidateAsync(feature.Candidates[1], CancellationToken.None);

        Assert.NotNull(feature.Binding);
        Assert.Equal("2", feature.Binding!.RemoteTitleId);
        Assert.Equal("g2", feature.Binding.PreferredGroupId);
        Assert.True(File.Exists(_bindingsPath));

        // The absolute url the provider handed over is stored as it is, and survives
        // the file. While the provider supplied a relative page path this field was
        // empty in every binding, because resolving a route is not this feature's
        // knowledge to guess.
        Assert.Equal("https://provider.test/title/hid-two", feature.Binding.CanonicalTitleUrl);
        var reloaded = Assert.Single(new SourceBindingStore(_bindingsPath).Load().Bindings);
        Assert.Equal("https://provider.test/title/hid-two", reloaded.CanonicalTitleUrl);
    }

    [Fact]
    public async Task TwoEmbeddedGroupsRequireOneGroupChoice()
    {
        WriteCbz("Chapter 1.cbz", chapterId: "c1", number: "1", groupId: "g1", groupName: "Official");
        WriteCbz("Chapter 1 [Scan].cbz", chapterId: "c9", number: "1", groupId: "g2", groupName: "Scanlation");

        var result = await Matcher(Mapping("12947", "dy88"))
            .MatchAsync(_library, Folder, CancellationToken.None);

        Assert.Equal(UpdateMatchKind.Ambiguous, result.Kind);
        Assert.Null(result.Match);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(["g1", "g2"], result.Candidates.Select(candidate => candidate.Group.GroupId));
    }

    [Fact]
    public async Task AnExistingLocalChapterIsNeverOfferedAgain()
    {
        // Chapter 1 is present with embedded identity; 2 and 3 are not present.
        WriteCbz("Chapter 1.cbz", chapterId: "c1", number: "1", groupId: "g1", groupName: "Official");

        _source.Groups["dy88"] = [Group("g1", "Official")];
        _source.Chapters["dy88|g1"] = Chapters("dy88", "g1", "1", "2", "3");

        UpdateDownloadRequest? handedOff = null;
        var feature = Feature(
            Mapping("12947", "dy88"),
            request =>
            {
                handedOff = request;
                return new UpdateHandoffResult(request.Chapters.Count, 0, null);
            });

        await feature.CheckAsync(Folder, CancellationToken.None);

        Assert.Equal(UpdateCheckState.Ready, feature.State);
        Assert.Equal(["c2", "c3"], feature.MissingChapters.Select(chapter => chapter.ChapterId));
        Assert.Equal(0, _source.BrowseCalls);

        // Only the explicitly selected chapter reaches the queue route.
        var result = feature.EnqueueSelected(["c3"]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Queued);
        Assert.NotNull(handedOff);
        Assert.Equal(Folder, handedOff!.LocalFolderName);
        Assert.Equal("g1", handedOff.Group.GroupId);
        Assert.Equal("Official", handedOff.GroupDisplayName);
        Assert.Equal("c3", Assert.Single(handedOff.Chapters).Identity.ChapterId);
    }

    /// <summary>
    /// The provider's title name is persisted with the binding, so a check run
    /// after a restart names a queued job the way the provider does instead of
    /// falling back to the local folder — and it does so without re-fetching the
    /// title detail just to recover a name it already stored.
    /// </summary>
    [Fact]
    public async Task ARestoredBindingStillNamesTheTitleTheWayTheProviderDoes()
    {
        WriteCbz("Chapter 1.cbz", chapterId: "c1", number: "1", groupId: "g1", groupName: "Official");

        _source.Groups["dy88"] = [Group("g1", "Official")];
        _source.Chapters["dy88|g1"] = Chapters("dy88", "g1", "1", "2");
        _source.TitleNames["dy88"] = "Provider Title Name";

        UpdateDownloadRequest? handedOff = null;
        var enqueue = new EnqueueUpdateChapters(request =>
        {
            handedOff = request;
            return new UpdateHandoffResult(request.Chapters.Count, 0, null);
        });

        var initial = Feature(Mapping("12947", "dy88"), enqueue);
        await initial.CheckAsync(Folder, CancellationToken.None);

        Assert.Equal(UpdateCheckState.Ready, initial.State);
        Assert.Equal("Provider Title Name", initial.Binding!.RemoteTitleName);
        Assert.True(_source.TitleCalls > 0);

        // A restart: a brand new feature and store over the same file.
        var titleCallsBeforeRestore = _source.TitleCalls;
        var restored = Feature(Mapping("12947", "dy88"), enqueue);
        await restored.CheckAsync(Folder, CancellationToken.None);

        Assert.Equal(UpdateCheckState.Ready, restored.State);
        Assert.Equal("Provider Title Name", restored.Binding!.RemoteTitleName);

        // The name came from storage, not from another provider call.
        Assert.Equal(titleCallsBeforeRestore, _source.TitleCalls);

        restored.EnqueueSelected(["c2"]);
        Assert.NotNull(handedOff);
        Assert.Equal("Provider Title Name", handedOff!.TitleDisplayName);
        Assert.NotEqual(Folder, handedOff.TitleDisplayName);
    }

    [Fact]
    public void TheBindingStoreUpsertsAtomicallyAndToleratesMalformedState()
    {
        var store = new SourceBindingStore(_bindingsPath);

        var first = store.Save(Binding("g1", "Official"));
        Assert.True(first.Saved);
        Assert.NotNull(store.Find(Folder));

        // One folder holds exactly one binding: a new group replaces the old one.
        Assert.True(store.Save(Binding("g2", "Scanlation")).Saved);
        var reloaded = new SourceBindingStore(_bindingsPath).Load();
        var binding = Assert.Single(reloaded.Bindings);
        Assert.Equal("g2", binding.PreferredGroupId);
        Assert.Null(reloaded.Warning);

        // A partial or hand-damaged file is a local warning, never a throw, and
        // reading it does not rewrite it.
        File.WriteAllText(_bindingsPath, "{ damaged");
        var damaged = new SourceBindingStore(_bindingsPath).Load();
        Assert.Empty(damaged.Bindings);
        Assert.NotNull(damaged.Warning);
        Assert.Equal("{ damaged", File.ReadAllText(_bindingsPath));

        // A binding missing its group cannot become an authority for the title.
        Assert.False(store.Save(new SourceBinding(
            ProviderId, "12947", "dy88", "Name", string.Empty, string.Empty, Folder,
            PreferredGroupId: " ", PreferredGroupName: "x", DateTimeOffset.UtcNow)).Saved);
    }

    private SourceBinding Binding(string groupId, string groupName) => new(
        ProviderId,
        "12947",
        "dy88",
        "Provider Title Name",
        "/title/dy88-local-title",
        string.Empty,
        Folder,
        groupId,
        groupName,
        DateTimeOffset.UtcNow);

    private Func<string, ConfirmedTitleMapping?> Mapping(string titleId, string titleHid) =>
        folderName => string.Equals(folderName, Folder, StringComparison.OrdinalIgnoreCase)
            ? new ConfirmedTitleMapping(ProviderId, titleId, titleHid, folderName)
            : null;

    /// <summary>
    /// Absolute, the way a provider adapter hands it over after resolving its own
    /// captured page path. Turning a relative provider route into a URL is provider
    /// knowledge, so this side only ever receives a value it can store as it is.
    /// </summary>
    private static RemoteTitleSummary Title(string titleId, string titleHid, string displayName) =>
        new(
            new RemoteTitleIdentity(ProviderId, titleId, titleHid, $"https://provider.test/title/{titleHid}"),
            displayName,
            null,
            null);

    private static RemoteSourceGroup Group(string groupId, string displayName) =>
        new(new RemoteGroupIdentity(ProviderId, groupId), displayName);

    private static List<RemoteChapterSummary> Chapters(
        string titleHid,
        string groupId,
        params string[] numbers)
    {
        var title = new RemoteTitleIdentity(ProviderId, "id-" + titleHid, titleHid, "/title/" + titleHid);
        var group = new RemoteGroupIdentity(ProviderId, groupId);
        return numbers
            .Select((number, index) => new RemoteChapterSummary(
                new RemoteChapterIdentity(ProviderId, title, "c" + number, number, group),
                "Chapter " + number,
                index))
            .ToList();
    }

    /// <summary>
    /// Writes one real ZIP-based chapter. Embedded Citadel provenance is written
    /// only when a chapter id is supplied, so the same helper produces both a
    /// Citadel-published archive and an older plain local archive.
    /// </summary>
    private void WriteCbz(
        string fileName,
        string? chapterId,
        string? number,
        string? groupId,
        string? groupName)
    {
        var path = Path.Combine(_library, Folder, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var page = archive.CreateEntry("001.png").Open())
        {
            page.Write([0x89, 0x50, 0x4E, 0x47]);
        }

        if (chapterId is null || groupId is null) return;

        using var writer = new StreamWriter(archive.CreateEntry(LocalTitleProbe.SourceManifestEntryName).Open());
        writer.Write(JsonSerializer.Serialize(new
        {
            Version = 1,
            Provider = ProviderId,
            TitleId = "12947",
            ChapterId = chapterId,
            ChapterNumber = number,
            GroupId = groupId,
            GroupName = groupName,
            PageCount = 1,
            ManifestHash = "sha256:test",
        }));
    }

    private sealed class FakeDirectory(FakeSource source) : IMangaSourceDirectory
    {
        public IReadOnlyList<IMangaSource> AvailableSources { get; } = [source];

        public IMangaSource? FindSource(string? sourceId) =>
            string.Equals(sourceId, source.Id, StringComparison.Ordinal) ? source : null;
    }

    /// <summary>
    /// Only the three calls update matching may make are answered; everything
    /// else throws so an unexpected provider interaction fails the test loudly
    /// instead of being silently absorbed.
    /// </summary>
    private sealed class FakeSource : IMangaSource
    {
        public string Id => ProviderId;

        public string DisplayName => "Fake provider";

        public MangaSourceCapabilities Capabilities { get; } = new(true, false, [], false);

        public List<RemoteTitleSummary> BrowseResults { get; set; } = [];

        public Dictionary<string, List<RemoteSourceGroup>> Groups { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<RemoteChapterSummary>> Chapters { get; } = new(StringComparer.Ordinal);

        public int BrowseCalls { get; private set; }

        public Task<RemoteCatalogPage> BrowseAsync(RemoteBrowseRequest request, CancellationToken cancellationToken)
        {
            BrowseCalls++;
            return Task.FromResult(new RemoteCatalogPage(BrowseResults, BrowseResults.Count, request.Page, false));
        }

        public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
            RemoteTitleIdentity title,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteSourceGroup>>(
                Groups.TryGetValue(title.TitleHid, out var groups) ? groups : []);

        public Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
            RemoteTitleIdentity title,
            RemoteGroupIdentity group,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteChapterSummary>>(
                Chapters.TryGetValue(title.TitleHid + "|" + group.GroupId, out var chapters) ? chapters : []);

        public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
            RemoteLookupKind kind, string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("update matching must not look up tags");

        public Dictionary<string, string> TitleNames { get; } = new(StringComparer.Ordinal);

        public int TitleCalls { get; private set; }

        public Task<RemoteTitleDetail> GetTitleAsync(
            RemoteTitleIdentity title,
            CancellationToken cancellationToken)
        {
            TitleCalls++;
            var name = TitleNames.TryGetValue(title.TitleHid, out var found)
                ? found
                : "Provider Title " + title.TitleId;
            return Task.FromResult(new RemoteTitleDetail(
                new RemoteTitleSummary(title, name, null, null),
                "description",
                [],
                []));
        }

        public Task<RemoteChapterManifest> GetManifestAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotSupportedException("update matching must not fetch a manifest");

        public Task<RemotePageImage> TransformPageAsync(
            RemotePage page, byte[] payload, CancellationToken cancellationToken) =>
            throw new NotSupportedException("update matching must not download pages");

        public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
            RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotSupportedException("update matching must not search alternate groups");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
