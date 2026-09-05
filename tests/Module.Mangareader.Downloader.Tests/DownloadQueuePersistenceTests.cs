using System.IO;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
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

    private DownloadQueueFeature CreateFeature(MangaSourceRegistry? sources = null)
    {
        var browser = new DownloaderPyHostClient(Path.Combine(_root, "downloads"));
        _disposables.Add(browser);
        var feature = new DownloadQueueFeature(
            new LibraryRootContext(new LibraryPathStore(Path.Combine(_root, "library-path.txt"))),
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
        Assert.True(index.IsPublished(chapter));

        // The same chapter number from another group is a distinct identity.
        Assert.False(index.IsPublished(chapter with { GroupId = "4725" }));
    }

    private static RemoteTitleIdentity Identity(string titleId, string hid) =>
        new("comix", titleId, hid, "same-display-title");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
