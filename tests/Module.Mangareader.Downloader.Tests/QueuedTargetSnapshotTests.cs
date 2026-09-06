using System.IO;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
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
    public async Task APublishedChapterNeedsConfirmedPermissionToBeQueuedAgain()
    {
        var (feature, _) = CreateFeature(out _);
        var index = new DownloadSourceIndex(_root);
        var published = await PublishAsync(feature, index);
        Assert.Empty(feature.Snapshot());

        // A caller that never asked keeps the skip, so an unconfirmed re-download
        // can neither create a duplicate nor overwrite a library file.
        var unconfirmed = feature.QueueChapters(Title(), Group(), [Chapter()], "Some Folder");

        Assert.Equal(0, unconfirmed.Queued);
        Assert.Equal(1, unconfirmed.SkippedAlreadyPublished);
        Assert.Equal(0, unconfirmed.SkippedAlreadyQueued);
        Assert.Empty(feature.Snapshot());

        // The permission a confirmed batch carries is what actually queues it. It
        // lands on the same target as the published chapter, which is what lets the
        // publisher replace the archive atomically instead of writing a "(2)".
        var confirmed = feature.QueueChapters(
            Title(),
            Group(),
            [Chapter()],
            "Some Folder",
            allowPublishedReplacement: true);

        Assert.Equal(1, confirmed.Queued);
        Assert.Equal(0, confirmed.SkippedAlreadyPublished);
        Assert.Null(confirmed.Blocked);

        var replacement = Assert.Single(feature.Snapshot());
        Assert.Equal(published.Identity, replacement.Identity);
        Assert.Equal(published.Target, replacement.Target);
    }

    /// <summary>
    /// The replacement permission covers chapters that are already published. It must
    /// not also produce a second live job for an identity whose previous job has not
    /// finished, which would download one chapter twice into one file.
    /// </summary>
    [Fact]
    public void AChapterWhoseJobHasNotFinishedIsNotQueuedAgainEvenWithPermission()
    {
        var (feature, _) = CreateFeature(out _, seededJobs: [PendingJob(LibraryFolder)]);

        // The restart rule parks a persisted job, and a parked job is not terminal.
        Assert.Equal(DownloadJobState.Paused, Assert.Single(feature.Snapshot()).State);

        var result = feature.QueueChapters(
            Title(),
            Group(),
            [Chapter()],
            "Some Folder",
            allowPublishedReplacement: true);

        Assert.Equal(0, result.Queued);
        Assert.Equal(1, result.SkippedAlreadyQueued);
        Assert.Equal(0, result.SkippedAlreadyPublished);
        Assert.Null(result.Blocked);
        Assert.Equal("pending", Assert.Single(feature.Snapshot()).JobId);
    }

    /// <summary>
    /// A publication record whose CBZ the user deleted is not evidence the Library
    /// still has the chapter. Trusting the record alone would skip the download and
    /// never show a confirmation, because the local probe correctly reports the
    /// chapter as absent — so the user could not re-download it at all.
    /// </summary>
    [Fact]
    public async Task APublishedRecordWhoseFileWasDeletedNoLongerBlocksTheDownload()
    {
        var (feature, _) = CreateFeature(out _);
        var index = new DownloadSourceIndex(_root);
        var published = await PublishAsync(feature, index);

        File.Delete(published.Target.FilePath);

        var result = feature.QueueChapters(Title(), Group(), [Chapter()], "Some Folder");

        Assert.Equal(1, result.Queued);
        Assert.Equal(0, result.SkippedAlreadyPublished);
        Assert.Null(result.Blocked);
        Assert.Single(feature.Snapshot());
    }

    /// <summary>
    /// Puts one chapter into the published state admission actually checks — the index
    /// record plus the file it names — and takes the seeding job out of the queue, so
    /// a later admission decision is about publication and not about a live job.
    /// </summary>
    private async Task<DownloadJobRecord> PublishAsync(
        DownloadQueueFeature feature,
        DownloadSourceIndex index)
    {
        var queued = feature.QueueChapters(Title(), Group(), [Chapter()], "Some Folder");
        Assert.Equal(1, queued.Queued);

        var job = Assert.Single(feature.Snapshot());
        feature.Remove(job.JobId);

        Assert.True(index.TryRecordPublished(job.Identity, job.Target.FileName, out _));
        Directory.CreateDirectory(Path.GetDirectoryName(job.Target.FilePath)!);
        await File.WriteAllBytesAsync(job.Target.FilePath, [1, 2, 3]);
        Assert.True(index.IsPublishedOnDisk(job.Identity, job.Target.Root, job.Target.FolderName));
        return job;
    }

    /// <summary>
    /// One persisted job for the same chapter identity the queue command builds, so
    /// admission sees work that has not finished. Queued on disk, which the restart
    /// rule parks — deterministic, and the scheduler is never started.
    /// </summary>
    private static DownloadJobRecord PendingJob(string libraryFolder) => new()
    {
        JobId = "pending",
        Identity = new DownloadJobIdentity("comix", "12947", "dy88", "chapter-143", "9897"),
        TitleDisplayName = "The Novel's Extra",
        ChapterDisplayName = "Chapter 143",
        GroupDisplayName = "Official",
        ChapterNumber = "143",
        Target = new DownloadTarget(libraryFolder, "Some Folder", "0143 - Chapter 143 [Official].cbz"),
        State = DownloadJobState.Queued,
        QueuedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private string LibraryFolder => Path.Combine(_root, "libraryA");

    private (DownloadQueueFeature Feature, LibraryRootContext Root) CreateFeature(
        out string libraryFolder,
        bool commitRoot = true,
        IReadOnlyList<DownloadJobRecord>? seededJobs = null)
    {
        libraryFolder = LibraryFolder;
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

        var store = new DownloadQueueStore(_root);
        if (seededJobs is not null) store.Save(seededJobs);

        // An empty registry keeps the scheduler from ever reaching a provider.
        var feature = new DownloadQueueFeature(
            libraryRoot,
            new MangaSourceRegistry([]),
            browser,
            store,
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
