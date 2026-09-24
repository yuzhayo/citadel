using System.IO;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Gate 4: pause-and-drain is the safe Stop barrier (quiesce admission, settle
/// actives, final Paused snapshot). Force abort is terminal, non-blocking, and
/// must never call the old synchronous ShutdownAsync wait.
/// </summary>
public sealed class DownloadQueuePauseDrainTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.PauseDrain.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    [Fact]
    public async Task PauseAndDrain_MarksRunnableAsPaused_AndRejectsStartResume()
    {
        Directory.CreateDirectory(_root);
        var store = new DownloadQueueStore(_root);
        store.Save([
            Job("queued", DownloadJobState.Paused),
            Job("done", DownloadJobState.Completed),
            Job("failed", DownloadJobState.Failed),
            Job("awaiting", DownloadJobState.AwaitingSourceFallback),
        ]);

        using var feature = CreateFeature();
        // Restart parks in-flight work; Start re-admits to Queued (no manifest).
        feature.Start("queued");
        Assert.Equal(DownloadJobState.Queued, StateOf(feature.Snapshot(), "queued"));

        await feature.PauseAndDrainAsync();

        var jobs = feature.Snapshot();
        Assert.Equal(DownloadJobState.Paused, StateOf(jobs, "queued"));
        // Barrier preserve set (PLAN Stop rules): Completed, Failed, and
        // decision-required AwaitingSourceFallback stay unchanged.
        Assert.Equal(DownloadJobState.Completed, StateOf(jobs, "done"));
        Assert.Equal(DownloadJobState.Failed, StateOf(jobs, "failed"));
        Assert.Equal(DownloadJobState.AwaitingSourceFallback, StateOf(jobs, "awaiting"));

        // Quiescing rejects admission until Dispose clears the instance.
        feature.Start("queued");
        Assert.Equal(DownloadJobState.Paused, StateOf(feature.Snapshot(), "queued"));
        feature.Resume("queued");
        Assert.Equal(DownloadJobState.Paused, StateOf(feature.Snapshot(), "queued"));
    }

    [Fact]
    public async Task PauseAndDrain_PreservesDecisionRequiredStates_AndStillQuiesces()
    {
        Directory.CreateDirectory(_root);
        var candidate = new SourceFallbackCandidate(
            "4725", "Asura Scans", "c-asura", "Chapter 1 tersedia dari Asura Scans.");
        new DownloadQueueStore(_root).Save([
            Job("failed", DownloadJobState.Failed),
            Job("awaiting", DownloadJobState.AwaitingSourceFallback) with
            {
                FallbackCandidates = [candidate],
            },
            Job("queued", DownloadJobState.Queued),
        ]);

        using var feature = CreateFeature();

        await feature.PauseAndDrainAsync();

        var jobs = feature.Snapshot();
        Assert.Equal([], jobs
            .Where(job => job.State is DownloadJobState.Pausing
                or DownloadJobState.Downloading
                or DownloadJobState.RefreshingManifest)
            .Select(job => job.JobId));

        Assert.Equal(DownloadJobState.Failed, StateOf(jobs, "failed"));
        var awaiting = jobs.Single(job => job.JobId == "awaiting");
        Assert.Equal(DownloadJobState.AwaitingSourceFallback, awaiting.State);
        Assert.Single(awaiting.FallbackCandidates);
        Assert.Equal(DownloadJobState.Paused, StateOf(jobs, "queued"));

        // Second drain is idempotent: preserve set is not flipped to Paused.
        await feature.PauseAndDrainAsync();
        var again = feature.Snapshot();
        Assert.Equal(DownloadJobState.Failed, StateOf(again, "failed"));
        Assert.Equal(
            DownloadJobState.AwaitingSourceFallback,
            StateOf(again, "awaiting"));
    }

    [Fact]
    public async Task PauseAndDrain_IsIdempotent_AndLeavesNoActiveWork()
    {
        Directory.CreateDirectory(_root);
        using var feature = CreateFeature();

        await feature.PauseAndDrainAsync();
        await feature.PauseAndDrainAsync();

        // Second drain after quiescing still succeeds (Stop retry semantics
        // clear quiescing only on failure; success keeps admission closed).
        Assert.Equal([], feature.Snapshot()
            .Where(job => job.State is DownloadJobState.Pausing
                or DownloadJobState.Downloading
                or DownloadJobState.RefreshingManifest)
            .Select(job => job.JobId));
    }

    [Fact]
    public async Task PauseAndDrain_PersistenceFailure_ClearsQuiescing_AndRethrows()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "queue.json");
        new DownloadQueueStore(_root).Save([Job("a", DownloadJobState.Paused)]);

        using var feature = CreateFeature();
        // Unknown schema: every Save from here on throws.
        File.WriteAllText(path, """{"Version":99,"Jobs":[]}""");

        await Assert.ThrowsAnyAsync<QueuePersistenceException>(
            () => feature.PauseAndDrainAsync());

        // Failure re-admits Resume: if quiescing were stuck, Resume would
        // return silently; reaching Commit throws the store conflict again.
        Assert.ThrowsAny<QueuePersistenceException>(() => feature.Resume("a"));
    }

    [Fact]
    public void ForceAbort_SetsTerminal_CancelsWithoutWait_AndSurvivesDispose()
    {
        Directory.CreateDirectory(_root);
        new DownloadQueueStore(_root).Save([
            Job("live", DownloadJobState.Paused),
            Job("done", DownloadJobState.Completed),
        ]);
        using var feature = CreateFeature();
        // Force persistence skips Completed/Paused rows; Start re-admits live
        // to Queued so the force-stop rewrite (Paused + warning) is observable.
        feature.Start("live");
        Assert.Equal(DownloadJobState.Queued, StateOf(feature.Snapshot(), "live"));

        var wall = System.Diagnostics.Stopwatch.StartNew();
        feature.ForceAbort(generation: 7);
        feature.Dispose(); // forced-terminal path: no ShutdownAsync wait
        wall.Stop();

        // Non-blocking: old Dispose did GetResult on scheduler shutdown.
        Assert.True(
            wall.Elapsed < TimeSpan.FromSeconds(2),
            $"force dispose blocked {wall.Elapsed}");

        // Force-stop persistence is background; wait briefly for the Paused write.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        DownloadJobRecord? live = null;
        while (DateTime.UtcNow < deadline)
        {
            live = feature.Snapshot().FirstOrDefault(job => job.JobId == "live");
            if (live?.State == DownloadJobState.Paused && live.Warning is not null) break;
            Thread.Sleep(20);
        }

        Assert.NotNull(live);
        Assert.Equal(DownloadJobState.Paused, live!.State);
        Assert.Contains("Force stopped", live.Warning, StringComparison.Ordinal);
        // Completed never rewritten.
        Assert.Equal(
            DownloadJobState.Completed,
            StateOf(feature.Snapshot(), "done"));
    }

    [Fact]
    public void Dispose_IsIdempotent_WithForceAbort()
    {
        Directory.CreateDirectory(_root);
        var feature = CreateFeature();
        feature.ForceAbort(1);
        feature.Dispose();
        feature.Dispose(); // second call is a no-op
        feature.DisposeAfterForceAbort(); // also a no-op once disposed
    }

    private DownloadQueueFeature CreateFeature()
    {
        var browser = new DownloaderPyHostClient(Path.Combine(_root, "downloads"));
        _disposables.Add(browser);

        var libraryRoot = new LibraryRootContext(
            new LibraryPathStore(Path.Combine(_root, "library-path.txt")));

        var feature = new DownloadQueueFeature(
            libraryRoot,
            MangaSourceRegistry.CreateDefault(browser),
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
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); }
            catch { /* force paths already terminal */ }
        }
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
