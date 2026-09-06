using System.IO;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Durable queue behavior: atomic round-trip, fail-soft corruption handling,
/// preservation of an unknown schema, and the restart rule that parks every
/// unfinished job instead of resuming it.
/// </summary>
public sealed class DownloadQueuePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    [Fact]
    public void QueueRoundTripsAtomicallyAndPreservesOrder()
    {
        var store = new DownloadQueueStore(_root);

        store.Save([Job("a", DownloadJobState.Paused), Job("b", DownloadJobState.Queued)]);
        var loaded = store.Load();

        Assert.Null(loaded.Warning);
        Assert.Null(loaded.UnsupportedVersion);
        Assert.Equal(["a", "b"], loaded.Jobs.Select(job => job.JobId));
        Assert.Equal(DownloadJobState.Paused, loaded.Jobs[0].State);
        Assert.Equal("Official", loaded.Jobs[0].GroupDisplayName);
    }

    [Fact]
    public void CorruptQueueIsReportedAndYieldsNoJobs()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "queue.json"), "{ this is not json");

        var loaded = new DownloadQueueStore(_root).Load();

        Assert.Empty(loaded.Jobs);
        Assert.NotNull(loaded.Warning);
    }

    [Fact]
    public void UnknownSchemaVersionIsPreservedRatherThanOverwritten()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "queue.json");
        File.WriteAllText(path, """{"Version":99,"Jobs":[]}""");
        var store = new DownloadQueueStore(_root);

        var loaded = store.Load();

        Assert.Equal(99, loaded.UnsupportedVersion);
        Assert.Empty(loaded.Jobs);
        Assert.Throws<QueueVersionConflictException>(() => store.Save([Job("a", DownloadJobState.Queued)]));
        Assert.Contains("99", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// The in-memory queue may only adopt a transition that became durable. An
    /// unpersisted mutation would report a state the file does not hold, so after a
    /// restart the job would be somewhere the user never saw it go.
    /// </summary>
    [Fact]
    public void ATransitionThatCannotBePersistedLeavesTheInMemoryQueueUntouched()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "queue.json");
        new DownloadQueueStore(_root).Save([Job("a", DownloadJobState.Paused)]);

        // An empty registry means the scheduler can never reach a provider, and a
        // parked job never starts it, so nothing else mutates the queue here.
        using var feature = CreateFeature(new MangaSourceRegistry([]));
        Assert.Equal(DownloadJobState.Paused, StateOf(feature.Snapshot(), "a"));

        var notifications = 0;
        feature.QueueSummaryChanged += (_, _) => notifications++;

        // The store's own refusal to write: an unrecognized schema is preserved
        // rather than replaced, so every save from here on throws.
        File.WriteAllText(path, """{"Version":99,"Jobs":[]}""");

        Assert.ThrowsAny<QueuePersistenceException>(() => feature.Resume("a"));

        Assert.Equal(DownloadJobState.Paused, StateOf(feature.Snapshot(), "a"));
        Assert.Equal(0, notifications);
        Assert.Contains("99", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelling the bounded work a pause interrupts is an effect, so it may only
    /// happen once the pause is durable. Cancelling first tells the user nothing
    /// changed while the download has already been stopped, leaving a job that still
    /// reads as running with no worker behind it.
    /// </summary>
    [Fact]
    public async Task APauseWhoseSaveFailedDoesNotCancelTheRunningDownload()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "queue.json");
        new DownloadQueueStore(_root).Save([Job("live", DownloadJobState.Paused)]);

        var source = new BlockingSource();
        using var feature = CreateFeature(new MangaSourceRegistry(
        [
            new MangaSourceRegistration(
                source,
                () => throw new NotSupportedException("the queue never builds a filter panel")),
        ]));

        // The resume is durable, so the scheduler picks the job up and parks inside
        // the provider call. Awaiting the rendezvous keeps this deterministic instead
        // of waiting and hoping.
        feature.Resume("live");
        await source.Entered.Task;

        Assert.Equal(DownloadJobState.Resolving, StateOf(feature.Snapshot(), "live"));
        Assert.True(source.ProviderToken.CanBeCanceled);

        // From here every save is refused: an unrecognized schema is preserved.
        File.WriteAllText(path, """{"Version":99,"Jobs":[]}""");

        Assert.ThrowsAny<QueuePersistenceException>(() => feature.Pause("live"));

        // Nothing about the pause may have happened: the state is unchanged and the
        // bounded work is still running.
        Assert.Equal(DownloadJobState.Resolving, StateOf(feature.Snapshot(), "live"));
        Assert.False(source.ProviderToken.IsCancellationRequested);
    }

    /// <summary>
    /// The duplicate decision belongs to the commit transaction. A snapshot taken
    /// beforehand let every concurrent admission pass the same identity and then all
    /// of them append it, which downloads one chapter twice into one file.
    ///
    /// This is deterministic rather than a stress loop: the admitted job parks inside
    /// the provider call, so it cannot become terminal mid-race and the outcome cannot
    /// depend on when the scheduler happens to run. Exactly one admission may win.
    /// </summary>
    [Fact]
    public void ConcurrentAdmissionsOfOneChapterProduceExactlyOneJob()
    {
        var source = new BlockingSource();
        using var feature = CreateFeature(
            new MangaSourceRegistry(
            [
                new MangaSourceRegistration(
                    source,
                    () => throw new NotSupportedException("the queue never builds a filter panel")),
            ]),
            commitRoot: true);

        var results = new QueueAddResult[16];
        Parallel.For(
            0,
            results.Length,
            index => results[index] = feature.QueueChapters(
                Title(), OfficialGroup(), [Chapter("143")], "Some Folder"));

        Assert.Equal(1, results.Sum(result => result.Queued));
        Assert.Equal(results.Length - 1, results.Sum(result => result.SkippedAlreadyQueued));
        Assert.All(results, result => Assert.Null(result.Blocked));

        var job = Assert.Single(feature.Snapshot());
        Assert.Equal("chapter-143", job.Identity.ChapterId);
        Assert.Equal("9897", job.Identity.GroupId);
    }

    /// <summary>
    /// A provider double that parks inside its manifest call, which is what puts a job
    /// genuinely in flight so the queue has a running cancellation source to protect.
    /// Every other call is unsupported: the queue must not reach for anything else.
    /// </summary>
    private sealed class BlockingSource : IMangaSource
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ProviderToken { get; private set; }

        public string Id => "comix";

        public string DisplayName => "Blocking";

        public MangaSourceCapabilities Capabilities { get; } = new(false, false, [], false);

        public Task<RemoteChapterManifest> GetManifestAsync(
            RemoteChapterIdentity chapter,
            CancellationToken cancellationToken)
        {
            ProviderToken = cancellationToken;
            var completion = new TaskCompletionSource<RemoteChapterManifest>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // Honours the queue's own shutdown, so disposing the feature does not
            // leave this test waiting on a call that will never answer.
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            Entered.TrySetResult();
            return completion.Task;
        }

        public Task<RemoteCatalogPage> BrowseAsync(RemoteBrowseRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the queue must not browse");

        public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(RemoteLookupKind kind, string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the queue must not look up tags");

        public Task<RemoteTitleDetail> GetTitleAsync(RemoteTitleIdentity title, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the queue must not open a title");

        public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(RemoteTitleIdentity title, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the queue must not list groups");

        public Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(RemoteTitleIdentity title, RemoteGroupIdentity group, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the queue must not list chapters");

        public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the queue must not search alternate groups");

        public Task<RemotePageImage> TransformPageAsync(RemotePage page, byte[] payload, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the queue must not transform a page");
    }

    [Fact]
    public void RestartParksEveryUnfinishedJobAndNeverResumes()
    {
        var store = new DownloadQueueStore(_root);
        store.Save(
        [
            Job("inflight", DownloadJobState.Downloading),
            Job("queued", DownloadJobState.Queued),
            Job("paused", DownloadJobState.Paused),
            Job("done", DownloadJobState.Completed),
            Job("broken", DownloadJobState.Failed),
        ]);

        using var feature = CreateFeature();
        var snapshot = feature.Snapshot();

        Assert.Equal(DownloadJobState.Paused, StateOf(snapshot, "inflight"));
        Assert.Equal(DownloadJobState.Paused, StateOf(snapshot, "queued"));
        Assert.Equal(DownloadJobState.Paused, StateOf(snapshot, "paused"));
        Assert.Equal(DownloadJobState.Completed, StateOf(snapshot, "done"));
        Assert.Equal(DownloadJobState.Failed, StateOf(snapshot, "broken"));
    }

    [Fact]
    public void RestartRecognizesAnAlreadyPublishedJobInsteadOfRedownloading()
    {
        Directory.CreateDirectory(_root);
        var published = Path.Combine(_root, "published.cbz");
        File.WriteAllBytes(published, [1, 2, 3]);

        var store = new DownloadQueueStore(_root);
        store.Save(
        [
            Job("renamed", DownloadJobState.Publishing) with { PublishedPath = published },
        ]);

        using var feature = CreateFeature();

        Assert.Equal(DownloadJobState.Completed, StateOf(feature.Snapshot(), "renamed"));
    }

    /// <summary>
    /// A persisted target is not trusted input. A stored segment that walks out of
    /// the job's own root is refused locally — the reason lands on the job, the
    /// reader keeps running, and nothing is written outside the Library — while a
    /// target the application created normally, spaces and all, keeps working under
    /// its own unchanged name.
    /// </summary>
    [Fact]
    public void APersistedTargetThatLeavesTheRootIsRefusedLocallyAndANormalOneStillWorks()
    {
        var library = Path.Combine(_root, "library root");
        Directory.CreateDirectory(library);

        var store = new DownloadQueueStore(_root);
        store.Save(
        [
            Job("escape", DownloadJobState.Queued) with
            {
                Target = new DownloadTarget(library, @"..\outside", "0001 - Chapter [Official].cbz"),
            },
            Job("normal", DownloadJobState.Queued) with
            {
                Target = new DownloadTarget(library, "Some Folder", "0001 - Chapter One [Official].cbz"),
            },
            Job("relative-root", DownloadJobState.Queued) with
            {
                Target = new DownloadTarget(@"..\somewhere", "Some Folder", "0001.cbz"),
            },
        ]);

        using var feature = CreateFeature(new MangaSourceRegistry([]));
        var snapshot = feature.Snapshot();

        var escape = snapshot.Single(job => job.JobId == "escape");
        Assert.Equal(DownloadJobState.Failed, escape.State);
        Assert.Contains(
            "Target download tidak aman",
            escape.Warning ?? string.Empty,
            StringComparison.Ordinal);

        // Refused, not repaired: the stored target is left exactly as it was, so the
        // user can see what was recorded and remove the job.
        Assert.Equal(@"..\outside", escape.Target.FolderName);

        // A relative root is refused before normalization, because resolving it
        // against the process working directory would silently aim the write at
        // somewhere the user never chose.
        var relativeRoot = snapshot.Single(job => job.JobId == "relative-root");
        Assert.Equal(DownloadJobState.Failed, relativeRoot.State);
        Assert.Contains("absolut", relativeRoot.Warning ?? string.Empty, StringComparison.Ordinal);

        var normal = snapshot.Single(job => job.JobId == "normal");
        Assert.Equal(DownloadJobState.Paused, normal.State);
        Assert.Equal("Some Folder", normal.Target.FolderName);
        Assert.Equal(Path.Combine(library, "Some Folder"), normal.Target.FolderPath);

        // The refusal happened before any write, so the escaped folder never appeared.
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Fact]
    public void SummaryCountsDriveTheCatalogBadge()
    {
        var store = new DownloadQueueStore(_root);
        store.Save(
        [
            Job("a", DownloadJobState.Downloading),
            Job("b", DownloadJobState.Paused),
            Job("c", DownloadJobState.Failed),
            Job("d", DownloadJobState.Completed),
        ]);

        using var feature = CreateFeature();
        var summary = feature.Summary();

        // The restart rule parks the in-flight job, so nothing is active and the
        // parked job joins the paused count.
        Assert.Equal(4, summary.Total);
        Assert.Equal(0, summary.Active);
        Assert.Equal(2, summary.Paused);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(3, summary.BadgeCount);
    }

    [Fact]
    public void CrossGroupFallbackReplacesTheWholeChapterOnlyAfterConfirmation()
    {
        var candidate = new SourceFallbackCandidate(
            "4725", "Asura Scans", "c-asura", "Chapter 1 tersedia dari Asura Scans.");
        var store = new DownloadQueueStore(_root);
        store.Save(
        [
            Job("f1", DownloadJobState.AwaitingSourceFallback) with
            {
                FallbackCandidates = [candidate],
            },
        ]);

        // An empty registry means the scheduler can never reach a provider, so
        // these assertions do not depend on timing.
        using var feature = CreateFeature(new MangaSourceRegistry([]));

        var before = feature.Snapshot().Single(job => job.JobId == "f1");
        Assert.Equal(DownloadJobState.AwaitingSourceFallback, before.State);
        Assert.Equal("9897", before.Identity.GroupId);

        feature.ConfirmSourceFallback("f1", candidate);
        var after = feature.Snapshot().Single(job => job.JobId == "f1");

        // The whole chapter moved: group, chapter identity and filename all
        // carry the alternate group, so no page can be mixed into the original
        // group's archive.
        Assert.Equal("4725", after.Identity.GroupId);
        Assert.Equal("c-asura", after.Identity.ChapterId);
        Assert.Equal("Asura Scans", after.GroupDisplayName);
        Assert.Empty(after.FallbackCandidates);
        Assert.Contains("[Asura Scans]", after.Target.FileName, StringComparison.Ordinal);
        Assert.NotEqual(before.Target.FileName, after.Target.FileName);
        Assert.NotEqual(DownloadJobState.AwaitingSourceFallback, after.State);
    }

    [Theory]
    [InlineData("1", "0001")]
    [InlineData("143", "0143")]
    [InlineData("10.5", "0010.5")]
    [InlineData("", "0000")]
    [InlineData("Special", "Special")]
    public void FilenamesAreDeterministicSanitizedAndCarryTheGroup(
        string chapterNumber,
        string expectedPrefix)
    {
        var fileName = DownloadQueueFeature.BuildFileName(Chapter(chapterNumber), OfficialGroup());

        Assert.StartsWith(expectedPrefix, fileName, StringComparison.Ordinal);
        Assert.EndsWith("[Official].cbz", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Path.GetInvalidFileNameChars(),
            fileName.Where(character => character != Path.DirectorySeparatorChar).ToArray());
    }

    [Fact]
    public void WholeChapterNumbersSortInReadingOrderNotTextOrder()
    {
        var names = new[] { "10", "2", "1", "143" }
            .Select(number => DownloadQueueFeature.BuildFileName(Chapter(number), OfficialGroup()))
            .ToList();

        var sorted = names
            .OrderBy(name => name, NaturalStringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(
            ["0001", "0002", "0010", "0143"],
            sorted.Select(name => name[..4]));
    }

    private static RemoteChapterSummary Chapter(string chapterNumber) =>
        new(
            new RemoteChapterIdentity(
                "comix",
                new RemoteTitleIdentity("comix", "12947", "dy88", "the-novels-extra"),
                "chapter-" + chapterNumber,
                chapterNumber,
                new RemoteGroupIdentity("comix", "9897")),
            "Chapter " + (chapterNumber.Length == 0 ? "0" : chapterNumber),
            0);

    private static RemoteSourceGroup OfficialGroup() =>
        new(new RemoteGroupIdentity("comix", "9897"), "Official");

    /// <summary>Carries the same identity the Chapter helper embeds, so the two agree.</summary>
    private static RemoteTitleSummary Title() =>
        new(
            new RemoteTitleIdentity("comix", "12947", "dy88", "the-novels-extra"),
            "The Novel's Extra",
            null,
            "Chapter 170");

    private DownloadQueueFeature CreateFeature(
        MangaSourceRegistry? sources = null,
        bool commitRoot = false)
    {
        var browser = new DownloaderPyHostClient(Path.Combine(_root, "downloads"));
        _disposables.Add(browser);

        var libraryRoot = new LibraryRootContext(
            new LibraryPathStore(Path.Combine(_root, "library-path.txt")));
        if (commitRoot)
        {
            var library = Path.Combine(_root, "library");
            Directory.CreateDirectory(library);
            libraryRoot.CompleteScan(
                libraryRoot.BeginScan(library),
                succeeded: true,
                cancelled: false);
        }

        var feature = new DownloadQueueFeature(
            libraryRoot,
            sources ?? MangaSourceRegistry.CreateDefault(browser),
            browser,
            new DownloadQueueStore(_root),
            new DownloadSourceIndex(_root));
        _disposables.Add(feature);
        return feature;
    }

    private static DownloadJobState StateOf(IReadOnlyList<DownloadJobRecord> jobs, string id) =>
        jobs.Single(job => job.JobId == id).State;

    private static DownloadJobRecord Job(string id, DownloadJobState state) => new()
    {
        JobId = id,
        Identity = new DownloadJobIdentity("comix", "t1", "h1", "c-" + id, "9897"),
        TitleDisplayName = "Title " + id,
        ChapterDisplayName = "Chapter " + id,
        GroupDisplayName = "Official",
        ChapterNumber = "1",
        Target = new DownloadTarget(
            Path.Combine(Path.GetTempPath(), "library"),
            "Title",
            "0001 - Chapter [Official].cbz"),
        State = state,
        QueuedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

/// <summary>
/// Confirmed mapping rules: identity, not display text, decides reuse, and a
/// mapping is only reused while its folder still exists.
/// </summary>
public sealed class DownloadSourceIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.Index.Tests",
        Guid.NewGuid().ToString("N"));

    public DownloadSourceIndexTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MappingIsReusedOnlyWhileItsFolderStillExists()
    {
        var index = new DownloadSourceIndex(_root);
        var identity = Identity("t1", "h1");
        index.ConfirmMapping(new SourceTitleMapping(
            "comix", "t1", "h1", "Mapped Folder", DateTimeOffset.UtcNow));

        // Folder absent: the mapping must not be reused, so the user confirms
        // a target again instead of publishing into a recreated folder.
        Assert.Null(index.FindUsableMapping(_root, identity));

        Directory.CreateDirectory(Path.Combine(_root, "Mapped Folder"));
        var found = index.FindUsableMapping(_root, identity);

        Assert.NotNull(found);
        Assert.Equal("Mapped Folder", found!.FolderName);
    }

    [Fact]
    public void IdenticalDisplayTitleWithDifferentIdentityIsNotTheSameMapping()
    {
        var index = new DownloadSourceIndex(_root);
        index.ConfirmMapping(new SourceTitleMapping(
            "comix", "t1", "h1", "Same Name", DateTimeOffset.UtcNow));
        Directory.CreateDirectory(Path.Combine(_root, "Same Name"));

        // Display-title equality is never evidence of identity.
        Assert.Null(index.FindUsableMapping(_root, Identity("t2", "h2")));
    }

    [Fact]
    public void PublishedIdentityIsRememberedPerGroup()
    {
        var index = new DownloadSourceIndex(_root);
        var chapter = new DownloadJobIdentity("comix", "t1", "h1", "c1", "9897");

        Assert.True(index.TryRecordPublished(chapter, "0001.cbz", out var warning));
        Assert.Null(warning);
        Assert.NotNull(index.FindPublished(chapter));

        // The same chapter number from another group is a distinct identity.
        Assert.Null(index.FindPublished(chapter with { GroupId = "4725" }));
    }

    private static RemoteTitleIdentity Identity(string titleId, string hid) =>
        new("comix", titleId, hid, "same-display-title");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
