using System.IO;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Library root ownership from the Downloader side: queueing needs a root, and a
/// job keeps the target it captured even when the Library root changes
/// afterwards, so an active job can never be silently redirected.
/// </summary>
public sealed class QueuedTargetSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.TargetSnapshot.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public QueuedTargetSnapshotTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void QueueingWithoutALibraryRootIsBlockedWithGuidance()
    {
        var (feature, libraryRoot) = CreateFeature(out _, commitRoot: false);
        Assert.Null(libraryRoot.CurrentRoot);

        var result = feature.QueueChapters(Title(), Group(), [Chapter()], "Some Folder");

        Assert.Equal(0, result.Queued);
        Assert.NotNull(result.Blocked);
        Assert.Empty(feature.Snapshot());
    }

    [Fact]
    public void AQueuedJobKeepsTheRootCapturedAtQueueTime()
    {
        var (feature, libraryRoot) = CreateFeature(out var folderA);

        var queued = feature.QueueChapters(Title(), Group(), [Chapter()], "Some Folder");
        Assert.Equal(1, queued.Queued);

        var job = Assert.Single(feature.Snapshot());
        Assert.Equal(folderA, job.Target.Root);
        Assert.Equal("Some Folder", job.Target.FolderName);

        // The Library root moves afterwards; the queued job must not follow.
        var folderB = Path.Combine(_root, "libraryB");
        Directory.CreateDirectory(folderB);
        libraryRoot.CompleteScan(libraryRoot.BeginScan(folderB), succeeded: true, cancelled: false);
        Assert.Equal(folderB, libraryRoot.CurrentRoot);

        var stillQueued = feature.Snapshot().Single(candidate => candidate.JobId == job.JobId);
        Assert.Equal(folderA, stillQueued.Target.Root);
    }

    [Fact]
    public void AnExistingMappingIsReusedAndAlreadyPublishedChaptersAreSkipped()
    {
        var (feature, _) = CreateFeature(out var folderA);
        Directory.CreateDirectory(Path.Combine(folderA, "Some Folder"));

        var first = feature.QueueChapters(Title(), Group(), [Chapter()], "Some Folder");
        Assert.Equal(1, first.Queued);

        // The same chapter identity is now published, so queueing it again is
        // skipped rather than creating a duplicate or a "(2)" file.
        var job = Assert.Single(feature.Snapshot());
        var index = new DownloadSourceIndex(_root);
        Assert.True(index.TryRecordPublished(job.Identity, job.Target.FileName, out _));

        var second = feature.QueueChapters(Title(), Group(), [Chapter()], "Some Folder");

        Assert.Equal(0, second.Queued);
        Assert.Equal(1, second.SkippedAlreadyPublished);
    }

    private (DownloadQueueFeature Feature, LibraryRootContext Root) CreateFeature(
        out string libraryFolder,
        bool commitRoot = true)
    {
        libraryFolder = Path.Combine(_root, "libraryA");
        Directory.CreateDirectory(libraryFolder);

        var libraryRoot = new LibraryRootContext(
            new LibraryPathStore(Path.Combine(_root, "library-path.txt")));
        if (commitRoot)
        {
            libraryRoot.CompleteScan(
                libraryRoot.BeginScan(libraryFolder),
                succeeded: true,
                cancelled: false);
        }

        var browser = new DownloaderPyHostClient(Path.Combine(_root, "downloads"));
        _disposables.Add(browser);

        // An empty registry keeps the scheduler from ever reaching a provider.
        var feature = new DownloadQueueFeature(
            libraryRoot,
            new MangaSourceRegistry([]),
            browser,
            new DownloadQueueStore(_root),
            new DownloadSourceIndex(_root));
        _disposables.Add(feature);
        return (feature, libraryRoot);
    }

    private static RemoteTitleSummary Title() =>
        new(
            new RemoteTitleIdentity("comix", "12947", "dy88", "the-novels-extra"),
            "The Novel's Extra",
            null,
            "Chapter 170");

    private static RemoteSourceGroup Group() =>
        new(new RemoteGroupIdentity("comix", "9897"), "Official");

    private static RemoteChapterSummary Chapter() =>
        new(
            new RemoteChapterIdentity(
                "comix",
                Title().Identity,
                "chapter-143",
                "143",
                new RemoteGroupIdentity("comix", "9897")),
            "Chapter 143",
            0);

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
