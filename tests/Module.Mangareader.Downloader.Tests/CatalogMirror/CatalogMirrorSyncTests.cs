using System.Diagnostics;
using System.IO;
using System.Linq;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Sync traversal boundary: Start/soft-Stop/Resume, ordered four-partition
/// traversal, checkpointing, generation guard and old-snapshot preservation,
/// through a fake source and the real store. No live network, no WPF, no
/// detail/cover/queue behavior.
/// </summary>
public sealed class CatalogMirrorSyncTests : IDisposable
{
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
    public async Task StartTraversesFourPartitionsInOrderThenActivates()
    {
        var calls = new List<string>();
        var source = new FakeSource((partition, page) =>
        {
            calls.Add(partition.Key + "/" + page);
            return Task.FromResult(Page(
                [Item(partition.Key + "-a"), Item(partition.Key + "-b")],
                page: 1, hasMore: false, total: 2));
        });
        var feature = Feature(source);
        var states = new List<CatalogSyncState>();
        feature.StateChanged += (_, progress) => states.Add(progress.State);

        await feature.StartAsync(CancellationToken.None);

        Assert.Equal(
            ["safe/1", "suggestive/1", "erotica/1", "pornographic/1"],
            calls);
        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        Assert.Equal(8, feature.Current.UniqueTitles);
        Assert.Contains(CatalogSyncState.Syncing, states);

        var active = await Store().TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(8, active.Value.Manifest.UniqueTitles);
    }

    [Fact]
    public async Task SoftStopParksAndResumeContinuesWithoutDuplicates()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new Dictionary<string, (CatalogSnapshotItem[] Items, bool HasMore)>
        {
            ["safe/1"] = ([Item("safe-a")], true),
            ["safe/2"] = ([Item("safe-b")], false),
            ["suggestive/2"] = ([Item("sug-b")], false),
            ["erotica/1"] = ([Item("er-a")], false),
            ["pornographic/1"] = ([Item("por-a")], false),
        };
        var calls = 0;
        var source = new FakeSource((partition, page) =>
        {
            calls++;
            if (partition.Key == "suggestive" && page == 1)
            {
                return gate.Task.ContinueWith(_ => Page(
                    [Item("sug-a")], page: 1, hasMore: false, total: 2),
                    CancellationToken.None);
            }

            var entry = script[partition.Key + "/" + page];
            return Task.FromResult(Page(entry.Items, page, entry.HasMore, total: 2));
        });
        var feature = Feature(source);

        var run = feature.StartAsync(CancellationToken.None);
        await WaitForAsync(() => calls >= 3);

        feature.RequestStop();
        Assert.Equal(CatalogSyncState.Stopping, feature.Current.State);
        gate.SetResult(true);
        await run;

        Assert.Equal(CatalogSyncState.Stopped, feature.Current.State);
        var checkpoint = await Store().LoadCheckpointAsync();
        Assert.NotNull(checkpoint);

        // The in-flight page still committed; Resume continues after it.
        await feature.ResumeAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        var active = await Store().TryLoadActiveAsync();
        Assert.NotNull(active);
        var ids = active.Value.Items.Select(item => item.TitleId).ToArray();
        Assert.Equal(ids.Distinct().Count(), ids.Length);
    }

    [Fact]
    public async Task InitializeRestoresStoppedCheckpointAfterRestart()
    {
        var store = Store();
        var checkpoint = await store.AppendPageAsync(
            new CatalogSnapshotPartition("safe", "safe"),
            0,
            Page([Item("safe-a")], page: 1, hasMore: true, total: 20),
            CancellationToken.None);
        var restarted = new CatalogMirrorSyncFeature(
            new FakeSource((partition, page) => Task.FromResult(Page(
                [Item(partition.Key + "-" + page)], page, hasMore: false, total: 20))),
            store);

        await restarted.InitializeAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Stopped, restarted.Current.State);
        Assert.Equal(checkpoint.PartitionKey, restarted.Current.PartitionKey);
        Assert.Equal(checkpoint.Page, restarted.Current.Page);
        Assert.Equal(checkpoint.StagedRecords, restarted.Current.StagedRecords);
        Assert.Single(restarted.Current.Partitions);
    }

    [Fact]
    public async Task SourceFailureKeepsPreviousSnapshotVisible()
    {
        var source = new FakeSource((_, _) => Task.FromResult(Page(
            [Item("v1")], page: 1, hasMore: false, total: 1)));
        var feature = Feature(source);
        await feature.StartAsync(CancellationToken.None);
        var manifest = await Store().TryLoadManifestAsync();
        Assert.NotNull(manifest);

        var failing = new FakeSource((_, _) => Task.FromException<CatalogSnapshotPage>(
            new CatalogSnapshotException("provider shape changed")));
        var retry = new CatalogMirrorSyncFeature(failing, Store());
        await retry.StartAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Error, retry.Current.State);
        Assert.Contains("provider shape changed", retry.Current.ErrorMessage);
        Assert.Equal("safe", retry.Current.PartitionKey);

        var kept = await Store().TryLoadManifestAsync();
        Assert.NotNull(kept);
        Assert.Equal(manifest.ActiveSnapshotId, kept.ActiveSnapshotId);
    }

    [Fact]
    public async Task SupersededRunStaysSilent()
    {
        var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var source = new FakeSource((partition, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return firstGate.Task.ContinueWith(_ => Page(
                    [Item("stale")], page: 1, hasMore: false, total: 1),
                    CancellationToken.None);
            }

            return Task.FromResult(Page(
                [Item(partition.Key + "-fresh")], page: 1, hasMore: false, total: 1));
        });
        var feature = Feature(source);
        var states = new List<CatalogSyncState>();
        feature.StateChanged += (_, progress) => states.Add(progress.State);

        var stale = feature.StartAsync(CancellationToken.None);
        await WaitForAsync(() => calls >= 1);

        // Supersede while the first fetch is in flight, then let it land late.
        var fresh = feature.ResumeAsync(CancellationToken.None);
        firstGate.SetResult(true);
        await Task.WhenAll(stale, fresh);

        Assert.DoesNotContain(CatalogSyncState.Error, states);
        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        var active = await Store().TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.DoesNotContain(active.Value.Items, item => item.TitleId == "stale");
    }

    [Fact]
    public async Task PoliteDelayPacesPagesWithoutSlowingTheFirst()
    {
        var source = new FakeSource((partition, _) => Task.FromResult(Page(
            [Item(partition.Key + "-a")], page: 1, hasMore: false, total: 1)));
        var feature = Feature(source);
        feature.PoliteDelay = TimeSpan.FromMilliseconds(300);

        var elapsed = Stopwatch.GetTimestamp();
        await feature.StartAsync(CancellationToken.None);
        var took = Stopwatch.GetElapsedTime(elapsed);

        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        // Four partitions, first page immediate, three paced gaps.
        Assert.True(took >= TimeSpan.FromMilliseconds(700), $"took {took}");
    }

    [Fact]
    public async Task ThrottleStopsAutomationSoTheHumanCanSolveTheChallenge()
    {
        var calls = 0;
        var source = new FakeSource((partition, _) =>
        {
            calls++;
            if (calls <= 2)
            {
                throw new ComixContractException("slow down") { HttpStatus = 429 };
            }

            return Task.FromResult(Page([Item("a")], page: 1, hasMore: false, total: 1));
        });
        var single = new SinglePartitionSource(source);
        var feature = new CatalogMirrorSyncFeature(single, Store());
        feature.PoliteDelay = TimeSpan.Zero;

        await feature.StartAsync(CancellationToken.None);

        // Safety net: a throttle stops the automated sync on the FIRST throttle
        // instead of retrying, so the human can solve the challenge in the
        // provider's headed browser and resume. Retrying here is what turned a
        // temporary challenge into sustained hammering and an IP block.
        Assert.Equal(1, calls);
        Assert.Equal(CatalogSyncState.Error, feature.Current.State);
        Assert.Null(await Store().TryLoadManifestAsync());
    }

    [Fact]
    public void ContractExceptionIsTheSingleSharedModuleType()
    {
        // Two same-named ComixContractException types once lived in two feature
        // namespaces; a cross-feature using bound the throttle handler to a type
        // the Catalog provider never throws, which silently disabled the stop-on-
        // throttle safety net while every test stayed green. One shared module-level
        // type keeps the safety net wired; a second same-named type fails here.
        var assembly = typeof(CatalogMirrorSyncFeature).Assembly;
        var matches = assembly.GetTypes().Where(t => t.Name == "ComixContractException").ToArray();

        Assert.Single(matches);
        Assert.Equal(typeof(ComixContractException), matches[0]);
        Assert.Equal("Module.Mangareader.Sources", matches[0].Namespace);
    }

    [Fact]
    public async Task ThrottleOn503AlsoStopsOnFirstAttempt()
    {
        var calls = 0;
        var source = new FakeSource((_, _) =>
        {
            calls++;
            throw new ComixContractException("slow down") { HttpStatus = 503 };
        });
        var feature = new CatalogMirrorSyncFeature(new SinglePartitionSource(source), Store());
        feature.PoliteDelay = TimeSpan.Zero;

        await feature.StartAsync(CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(CatalogSyncState.Error, feature.Current.State);
        Assert.Null(await Store().TryLoadManifestAsync());
    }

    [Fact]
    public async Task StopWakesThePoliteDelayImmediately()
    {
        var calls = 0;
        var source = new FakeSource((partition, _) =>
        {
            calls++;
            return Task.FromResult(Page(
                [Item(partition.Key + "-" + calls)], page: calls, hasMore: calls < 2, total: 2));
        });
        var feature = new CatalogMirrorSyncFeature(new SinglePartitionSource(source), Store());
        feature.PoliteDelay = TimeSpan.FromMinutes(1);

        var run = feature.StartAsync(CancellationToken.None);
        await WaitForAsync(() => calls >= 1);
        feature.RequestStop();
        var elapsed = Stopwatch.GetTimestamp();
        await run;

        Assert.Equal(CatalogSyncState.Stopped, feature.Current.State);
        Assert.True(
            Stopwatch.GetElapsedTime(elapsed) < TimeSpan.FromSeconds(5),
            "stop waited out the polite delay");
    }

    [Fact]
    public async Task RefreshStopsAtFirstFullyKnownPage()
    {
        var script = new Dictionary<string, (CatalogSnapshotItem[] Items, bool HasMore)>
        {
            // Baseline run.
            ["base|safe/1"] = ([Item("k1"), Item("k2")], false),
            ["base|suggestive/1"] = ([Item("k3")], false),
            ["base|erotica/1"] = ([Item("k4")], false),
            ["base|pornographic/1"] = ([Item("k5")], false),
            // Refresh run: one new title, then a fully known page stops safe.
            ["delta|safe/1"] = ([Item("n1"), Item("k1")], true),
            ["delta|safe/2"] = ([Item("k1"), Item("k2")], true),
            ["delta|suggestive/1"] = ([Item("k3")], false),
            ["delta|erotica/1"] = ([Item("k4")], false),
            ["delta|pornographic/1"] = ([Item("k5")], false),
        };
        var phase = "base";
        var calls = new List<string>();
        var source = new FakeSource((partition, page) =>
        {
            var key = phase + "|" + partition.Key + "/" + page;
            calls.Add(key);
            var entry = script[key];
            return Task.FromResult(Page(entry.Items, page, entry.HasMore, total: 10));
        });
        var feature = Feature(source);
        feature.PoliteDelay = TimeSpan.Zero;

        await feature.StartAsync(CancellationToken.None);
        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);

        phase = "delta";
        calls.Clear();
        await feature.RefreshAsync(CancellationToken.None);
        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        // Overlap stopped safe after page 2; no page 3 was ever requested.
        Assert.Equal(
            ["delta|safe/1", "delta|safe/2", "delta|suggestive/1", "delta|erotica/1", "delta|pornographic/1"],
            calls);

        var manifest = await Store().TryLoadManifestAsync();
        Assert.NotNull(manifest);
        Assert.Equal(6, manifest.UniqueTitles);
        Assert.NotNull(manifest.LastDeltaUtc);
    }

    [Fact]
    public async Task RefreshWithoutSnapshotReportsError()
    {
        var feature = Feature(new FakeSource((_, _) => Task.FromResult(
            Page([Item("a")], page: 1, hasMore: false, total: 1))));
        feature.PoliteDelay = TimeSpan.Zero;

        await feature.RefreshAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Error, feature.Current.State);
        Assert.Contains("no active snapshot", feature.Current.ErrorMessage);
    }

    [Fact]
    public async Task RefreshUpsertsWithoutDriftWarnings()
    {
        var phase = "base";
        var source = new FakeSource((partition, page) => Task.FromResult(
            phase == "base"
                ? Page([Item("a", "Old"), Item("b")], page: 1, hasMore: false, total: 2)
                : partition.Key == "safe" && page == 1
                    ? Page([Item("a", "New")], page: 1, hasMore: false, total: 2)
                    : Page([], page: 1, hasMore: false, total: 0)));
        var feature = Feature(source);
        feature.PoliteDelay = TimeSpan.Zero;
        await feature.StartAsync(CancellationToken.None);

        phase = "delta";
        await feature.RefreshAsync(CancellationToken.None);

        var active = await Store().TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(2, active.Value.Manifest.UniqueTitles);
        Assert.Equal("New", active.Value.Items.First(item => item.TitleId == "a").Title);
        Assert.Empty(active.Value.Manifest.Warnings);
        Assert.NotNull(active.Value.Manifest.LastDeltaUtc);

        // A later backfill clears the watermark.
        phase = "base";
        await feature.StartAsync(CancellationToken.None);
        var backfilled = await Store().TryLoadManifestAsync();
        Assert.NotNull(backfilled);
        Assert.Null(backfilled.LastDeltaUtc);
    }

    [Fact]
    public async Task RefreshStopResumeContinuesWithoutRefetch()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new Dictionary<string, (CatalogSnapshotItem[] Items, bool HasMore)>
        {
            ["base|safe/1"] = ([Item("k1"), Item("k2")], false),
            ["base|suggestive/1"] = ([Item("k3")], false),
            ["base|erotica/1"] = ([Item("k4")], false),
            ["base|pornographic/1"] = ([Item("k5")], false),
            ["delta|safe/2"] = ([Item("k1"), Item("k2")], true),
            ["delta|suggestive/1"] = ([Item("k3")], false),
            ["delta|erotica/1"] = ([Item("k4")], false),
            ["delta|pornographic/1"] = ([Item("k5")], false),
        };
        var phase = "base";
        var calls = new List<string>();
        var source = new FakeSource((partition, page) =>
        {
            var key = phase + "|" + partition.Key + "/" + page;
            calls.Add(key);
            if (key == "delta|safe/1")
            {
                return gate.Task.ContinueWith(_ => Page(
                    [Item("n1"), Item("k1")], page: 1, hasMore: true, total: 10),
                    CancellationToken.None);
            }

            var entry = script[key];
            return Task.FromResult(Page(entry.Items, page, entry.HasMore, total: 10));
        });
        var feature = Feature(source);
        feature.PoliteDelay = TimeSpan.Zero;

        await feature.StartAsync(CancellationToken.None);
        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);

        phase = "delta";
        calls.Clear();
        var run = feature.RefreshAsync(CancellationToken.None);
        await WaitForAsync(() => calls.Contains("delta|safe/1"));
        feature.RequestStop();
        gate.SetResult(true);
        await run;

        Assert.Equal(CatalogSyncState.Stopped, feature.Current.State);
        var checkpoint = await Store().LoadCheckpointAsync();
        Assert.NotNull(checkpoint);
        Assert.Equal(0, checkpoint.PartitionIndex);
        Assert.Equal(1, checkpoint.Page);

        await feature.ResumeAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        Assert.Equal(1, calls.Count(call => call == "delta|safe/1"));
        var active = await Store().TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(6, active.Value.Manifest.UniqueTitles);
        var ids = active.Value.Items.Select(item => item.TitleId).ToArray();
        Assert.Equal(ids.Distinct().Count(), ids.Length);
    }

    [Fact]
    public async Task RefreshStopAfterOverlapResumesWithoutReenteringPartition()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new Dictionary<string, (CatalogSnapshotItem[] Items, bool HasMore)>
        {
            ["base|safe/1"] = ([Item("k1"), Item("k2")], false),
            ["base|suggestive/1"] = ([Item("k3")], false),
            ["base|erotica/1"] = ([Item("k4")], false),
            ["base|pornographic/1"] = ([Item("k5")], false),
            ["delta|safe/1"] = ([Item("n1")], true),
            ["delta|safe/2"] = ([Item("k1"), Item("k2")], true),
            ["delta|suggestive/2"] = ([Item("k3")], false),
            ["delta|erotica/1"] = ([Item("k4")], false),
            ["delta|pornographic/1"] = ([Item("k5")], false),
        };
        var phase = "base";
        var calls = new List<string>();
        var source = new FakeSource((partition, page) =>
        {
            var key = phase + "|" + partition.Key + "/" + page;
            calls.Add(key);
            if (key == "delta|suggestive/1")
            {
                return gate.Task.ContinueWith(_ => Page(
                    [Item("k3")], page: 1, hasMore: false, total: 10),
                    CancellationToken.None);
            }

            var entry = script[key];
            return Task.FromResult(Page(entry.Items, page, entry.HasMore, total: 10));
        });
        var feature = Feature(source);
        feature.PoliteDelay = TimeSpan.Zero;

        await feature.StartAsync(CancellationToken.None);
        phase = "delta";
        calls.Clear();
        var run = feature.RefreshAsync(CancellationToken.None);
        await WaitForAsync(() => calls.Contains("delta|suggestive/1"));
        feature.RequestStop();
        gate.SetResult(true);
        await run;

        Assert.Equal(CatalogSyncState.Stopped, feature.Current.State);
        // The in-flight page completed fully (commit, overlap, boundary
        // marker) before the stop took effect, so resume continues at erotica.
        var checkpoint = await Store().LoadCheckpointAsync();
        Assert.NotNull(checkpoint);
        Assert.Equal(2, checkpoint.PartitionIndex);
        Assert.Equal(0, checkpoint.Page);

        var safeCalls = calls.Count(call => call.StartsWith("delta|safe/", StringComparison.Ordinal));
        await feature.ResumeAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        Assert.Equal(
            safeCalls,
            calls.Count(call => call.StartsWith("delta|safe/", StringComparison.Ordinal)));
        var active = await Store().TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(6, active.Value.Manifest.UniqueTitles);
    }

    [Fact]
    public async Task ProgressCarriesPartitionTable()
    {
        var source = new FakeSource((partition, _) => Task.FromResult(Page(
            [Item(partition.Key + "-a")], page: 1, hasMore: false, total: 7)));
        var feature = Feature(source);
        feature.PoliteDelay = TimeSpan.Zero;

        await feature.StartAsync(CancellationToken.None);

        Assert.Equal(CatalogSyncState.Ready, feature.Current.State);
        Assert.Equal(4, feature.Current.Partitions.Count);
        Assert.All(feature.Current.Partitions, part =>
        {
            Assert.Equal(1, part.Page);
            Assert.Equal(1, part.StagedRecords);
            Assert.Equal(7, part.ProviderTotal);
        });
        Assert.Equal(
            ["safe", "suggestive", "erotica", "pornographic"],
            feature.Current.Partitions.Select(part => part.PartitionKey).ToArray());
    }

    private CatalogMirrorSyncFeature Feature(FakeSource source) =>
        new(source, Store());

    /// <summary>
    /// Bounded wait: a regression must fail fast instead of hanging the suite.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the sync to progress.");
            }

            await Task.Delay(25);
        }
    }

    private CatalogSnapshotStore Store() =>
        new(new CatalogMirrorPaths(_root));

    private sealed class FakeSource(
        Func<CatalogSnapshotPartition, int, Task<CatalogSnapshotPage>> fetch)
        : ICatalogSnapshotSource
    {        public string SourceId => "fake";

        public IReadOnlyList<CatalogSnapshotPartition> SnapshotPartitions { get; } =
        [
            new("safe", "safe"),
            new("suggestive", "suggestive"),
            new("erotica", "erotica"),
            new("pornographic", "pornographic"),
        ];

        public Task<CatalogSnapshotPage> GetSnapshotPageAsync(
            CatalogSnapshotPartition partition,
            int page,
            CancellationToken cancellationToken,
            bool latestFirst = false) =>
            fetch(partition, page);
    }

    /// <summary>One-partition view over a FakeSource fetch for tight scenarios.</summary>
    private sealed class SinglePartitionSource(FakeSource inner) : ICatalogSnapshotSource
    {
        public string SourceId => "fake";

        public IReadOnlyList<CatalogSnapshotPartition> SnapshotPartitions { get; } =
            [new("safe", "safe")];

        public Task<CatalogSnapshotPage> GetSnapshotPageAsync(
            CatalogSnapshotPartition partition,
            int page,
            CancellationToken cancellationToken,
            bool latestFirst = false) =>
            inner.GetSnapshotPageAsync(partition, page, cancellationToken);
    }

    private static CatalogSnapshotPage Page(
        CatalogSnapshotItem[] items, int page, bool hasMore, long? total) =>
        new(items, total, page, hasMore);

    private static CatalogSnapshotItem Item(string id, string? title = null) => new(
        SourceId: "fake",
        TitleId: id,
        TitleHid: "h-" + id,
        CanonicalUrl: "https://example.invalid/title/h-" + id,
        Title: title ?? "Title " + id,
        AlternateTitles: [],
        CoverUrl: null,
        LatestChapterValue: 1,
        LatestChapterLabel: "Ch. 1",
        Rating: "safe",
        Type: null,
        Status: null,
        Language: null,
        Year: null,
        Synopsis: null,
        CapturedAtUtc: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        IsTitlePlaceholder: false);
}
