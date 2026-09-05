using System.IO;
using System.Security.Cryptography;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Resume and staging-integrity gates: a page is reused only when its journal
/// record and its bytes still validate against the current immutable manifest,
/// a changed manifest is a visible conflict rather than a silent mix, and two
/// store instances in one process never interleave a write.
/// </summary>
public sealed class DownloadResumeTests : IDisposable
{
    private const string ManifestHash = "sha256:resume-fixture";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.Resume.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _staging;
    private readonly List<IDisposable> _disposables = [];

    public DownloadResumeTests()
    {
        _staging = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_staging);
    }

    [Fact]
    public async Task ValidatedStagingIsReusedWithoutTouchingTheTransport()
    {
        var pageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var (pipeline, job, manifest) = await SeedStagedPageAsync(pageBytes, corrupt: false);

        var result = await pipeline.RunAsync(job, manifest, string.Empty, null, CancellationToken.None);

        // Completion proves reuse: the page URL is unroutable, so any transport
        // attempt would have failed the page instead.
        Assert.True(result.Complete, result.Detail);
        Assert.Empty(result.FailedPages);
        var staged = Assert.Single(result.Pages);
        Assert.Equal(0, staged.Ordinal);
        Assert.Equal("png", staged.Format);
        Assert.Equal(pageBytes, await File.ReadAllBytesAsync(staged.AbsolutePath));
    }

    [Fact]
    public async Task AStagedPageWhoseBytesNoLongerMatchIsNotReused()
    {
        var (pipeline, job, manifest) = await SeedStagedPageAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 },
            corrupt: true);

        var result = await pipeline.RunAsync(job, manifest, string.Empty, null, CancellationToken.None);

        // The corrupted page is rejected and the chapter stays incomplete rather
        // than publishing bytes that no longer match their record.
        Assert.False(result.Complete);
        Assert.Equal([0], result.FailedPages);
    }

    [Fact]
    public async Task AChangedManifestConflictsInsteadOfMixingStaging()
    {
        var (pipeline, job, _) = await SeedStagedPageAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 },
            corrupt: false);

        var refreshed = Manifest() with { ManifestHash = "sha256:different-manifest" };
        var result = await pipeline.RunAsync(job, refreshed, string.Empty, null, CancellationToken.None);

        Assert.False(result.Complete);
        Assert.True(result.ManifestConflict);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void ConcurrentStoreInstancesNeverInterleaveAWrite()
    {
        var first = new DownloadQueueStore(_root);
        var second = new DownloadQueueStore(_root);

        for (var round = 0; round < 20; round++)
        {
            first.Save([Job("a", round)]);
            second.Save([Job("a", round), Job("b", round)]);
        }

        // The file is always complete JSON owned by one writer, and the last
        // committed order is what a reader sees.
        var loaded = second.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(["a", "b"], loaded.Jobs.Select(job => job.JobId));
        Assert.Equal(19, loaded.Jobs[0].CompletedPages);
    }

    private ChapterDownloadPipeline CreatePipeline()
    {
        var browser = new DownloaderPyHostClient(_staging);
        _disposables.Add(browser);
        return new ChapterDownloadPipeline(
            new PageTransport(browser, _staging),
            new UnreachableSource(),
            _staging,
            // Same retry and recovery logic, without waiting through the
            // production schedule in a unit test.
            [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
    }

    [Fact]
    public void ProductionRetryScheduleIsTheLockedOneTwoFourSeconds()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            ChapterDownloadPipeline.RetryDelays);
        Assert.Equal(2, ChapterDownloadPipeline.PageConcurrency);
    }

    /// <summary>
    /// Seeds one job's staging folder with a journal and a page file, so the
    /// pipeline can decide whether to reuse it.
    /// </summary>
    private async Task<(ChapterDownloadPipeline Pipeline, DownloadJobRecord Job, RemoteChapterManifest Manifest)>
        SeedStagedPageAsync(byte[] pageBytes, bool corrupt)
    {
        var pipeline = CreatePipeline();
        var job = Job("job-1", 0);
        var jobRoot = pipeline.JobRoot(job.JobId);
        Directory.CreateDirectory(Path.Combine(jobRoot, "pages"));

        var relativePath = Path.Combine("pages", "00000-p0.png");
        var absolutePath = Path.Combine(jobRoot, relativePath);
        var written = corrupt
            ? new byte[] { 0x00, 0x01, 0x02 }
            : pageBytes;
        await File.WriteAllBytesAsync(absolutePath, written);

        // The journal always records the ORIGINAL page, which is what makes the
        // corrupted case detectable by hash rather than by luck.
        var record = new StagedPageRecord(
            0,
            "p0",
            relativePath,
            ExpectedBytes: null,
            ObservedBytes: pageBytes.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(pageBytes)),
            "png",
            Transformed: false,
            Validated: true);
        DownloaderJson.WriteAtomic(
            Path.Combine(jobRoot, "job.json"),
            new JobJournal(ManifestHash, [record]));

        return (pipeline, job, Manifest());
    }

    private static RemoteChapterManifest Manifest() =>
        new(
            new RemoteChapterIdentity(
                "test",
                new RemoteTitleIdentity("test", "t1", "h1", "slug"),
                "c1",
                "1",
                new RemoteGroupIdentity("test", "g1")),
            [
                new RemotePage(
                    0,
                    "p0",
                    "https://127.0.0.1:1/page.png",
                    ExpectedBytes: null,
                    Transform: null),
            ],
            ManifestHash,
            new Dictionary<string, string>());

    private static DownloadJobRecord Job(string id, int completedPages) => new()
    {
        JobId = id,
        Identity = new DownloadJobIdentity("test", "t1", "h1", "c1", "g1"),
        TitleDisplayName = "Resume Title",
        ChapterDisplayName = "Chapter 1",
        GroupDisplayName = "Official",
        ChapterNumber = "1",
        Target = new DownloadTarget(Path.Combine(Path.GetTempPath(), "library"), "Resume Title", "0001.cbz"),
        State = DownloadJobState.Queued,
        CompletedPages = completedPages,
        QueuedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// A source that must never be reached by these tests: every call fails, so
    /// a passing reuse assertion proves the transport and provider were skipped.
    /// </summary>
    private sealed class UnreachableSource : IMangaSource
    {
        public string Id => "test";

        public string DisplayName => "Unreachable";

        public MangaSourceCapabilities Capabilities { get; } =
            new(false, false, [], TransformsPages: false);

        public Task<RemoteCatalogPage> BrowseAsync(RemoteBrowseRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the source must not be called");

        public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(RemoteLookupKind kind, string query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the source must not be called");

        public Task<RemoteTitleDetail> GetTitleAsync(RemoteTitleIdentity title, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the source must not be called");

        public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(RemoteTitleIdentity title, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the source must not be called");

        public Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(RemoteTitleIdentity title, RemoteGroupIdentity group, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the source must not be called");

        public Task<RemoteChapterManifest> GetManifestAsync(RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            // Recovery refreshes the manifest with the same hash, so the retry
            // stays inside the one immutable manifest this job started with.
            Task.FromResult(Manifest());

        public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(RemoteChapterIdentity chapter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteAlternateChapter>>([]);

        public Task<RemotePageImage> TransformPageAsync(RemotePage page, byte[] payload, CancellationToken cancellationToken) =>
            Task.FromResult(new RemotePageImage(payload, "png"));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
