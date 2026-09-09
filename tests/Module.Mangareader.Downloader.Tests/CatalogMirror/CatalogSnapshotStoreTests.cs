using System.IO;
using Microsoft.Data.Sqlite;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Snapshot store verification boundary: recovery, bounds, checkpoint
/// durability order, dedupe, activation, retention and safe cleanup. No WPF,
/// no live provider, no query/detail/queue behavior.
/// </summary>
public sealed class CatalogSnapshotStoreTests : IDisposable
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
    public async Task AppendPageCommitsPageAndCheckpointAtomically()
    {
        var store = Store();
        var checkpoint = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a"), Item("b")], hasMore: true), CancellationToken.None);

        Assert.Equal(1, checkpoint.Page);
        Assert.Equal(2, checkpoint.StagedRecords);
        Assert.Equal("safe", checkpoint.PartitionKey);

        // A second instance over the same root sees the durable checkpoint.
        var reloaded = await Store().LoadCheckpointAsync();
        Assert.Equal(checkpoint, reloaded);
    }

    [Fact]
    public async Task AppendPageRejectsGapsDuplicatesAndBackwardPartitions()
    {
        var store = Store();
        await store.AppendPageAsync(Partition("safe"), 0, Page(1, [Item("a")], hasMore: true), CancellationToken.None);

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("b")], hasMore: true), CancellationToken.None));
        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(3, [Item("b")], hasMore: true), CancellationToken.None));

        var checkpoint = await store.AppendPageAsync(
            Partition("suggestive"), 1, Page(1, [Item("c")], hasMore: false), CancellationToken.None);
        Assert.Equal(1, checkpoint.PartitionIndex);

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(2, [Item("d")], hasMore: false), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyPageWithHasMoreIsContractErrorWithoutCommit()
    {
        var store = Store();

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [], hasMore: true), CancellationToken.None));

        Assert.Null(await store.LoadCheckpointAsync());
    }

    [Fact]
    public async Task MissingIdentityFailsPageWithoutCommit()
    {
        var store = Store();
        var bad = Item("a") with { TitleId = " " };

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [bad], hasMore: false), CancellationToken.None));

        Assert.Null(await store.LoadCheckpointAsync());
    }

    [Fact]
    public async Task BlankTitleRequiresPlaceholderFlag()
    {
        var store = Store();
        var blank = Item("a") with { Title = " " };

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [blank], hasMore: false), CancellationToken.None));

        var placeholder = Item("a") with
        {
            Title = CatalogSnapshotTitles.PlaceholderFor("h-a"),
            IsTitlePlaceholder = true,
        };
        var checkpoint = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [placeholder], hasMore: false), CancellationToken.None);
        Assert.Equal(1, checkpoint.StagedRecords);
    }

    [Fact]
    public async Task OversizedRecordAndPageBoundAreRejected()
    {
        var store = Store();
        var big = Item("a") with { Synopsis = new string('x', CatalogSnapshotLimits.MaxRecordBytes) };

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [big], hasMore: false), CancellationToken.None));

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(CatalogSnapshotLimits.MaxPagesPerPartition + 1, [Item("a")], hasMore: false),
            CancellationToken.None));

        Assert.Null(await store.LoadCheckpointAsync());
    }

    [Fact]
    public async Task RepeatedPageFingerprintIsPaginationLoop()
    {
        var store = Store();
        var items = new[] { Item("a"), Item("b") };
        await store.AppendPageAsync(Partition("safe"), 0, Page(1, items, hasMore: true), CancellationToken.None);

        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(2, items, hasMore: false), CancellationToken.None));
    }

    [Fact]
    public async Task PartitionProgressAccumulatesPerPartition()
    {
        var store = Store();
        var paths = new CatalogMirrorPaths(_root);
        await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a"), Item("b")], hasMore: true, total: 100),
            CancellationToken.None);
        await store.AppendPageAsync(
            Partition("safe"), 0, Page(2, [Item("c")], hasMore: false, total: 100),
            CancellationToken.None);

        var database = new CatalogMirrorDatabase(paths);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT partition_key, partition_index, page, staged_records, provider_total
            FROM catalog_partition_checkpoint;
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("safe", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(3, reader.GetInt64(3));
        Assert.Equal(100, reader.GetInt64(4));
        Assert.False(reader.Read());

        var checkpoint = await store.LoadCheckpointAsync();
        Assert.NotNull(checkpoint);
        Assert.Equal(3, checkpoint.StagedRecords);
    }

    [Fact]
    public async Task CheckpointSurvivesRestart()
    {
        var store = Store();
        await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a"), Item("b")], hasMore: true), CancellationToken.None);

        // A new instance (process restart) resumes exactly where the old one stopped.
        var resumed = Store();
        var checkpoint = await resumed.LoadCheckpointAsync();
        Assert.NotNull(checkpoint);
        Assert.Equal(1, checkpoint.Page);
        Assert.Equal(2, checkpoint.StagedRecords);

        var next = await resumed.AppendPageAsync(
            Partition("safe"), 0, Page(2, [Item("c")], hasMore: false), CancellationToken.None);
        Assert.Equal(3, next.StagedRecords);
    }

    [Fact]
    public async Task ActivationDedupesLastPartitionWinsWithDriftWarning()
    {
        var store = Store();
        var first = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a", title: "A-safe")], hasMore: false),
            CancellationToken.None);
        var second = await store.AppendPageAsync(
            Partition("suggestive"), 1, Page(1, [Item("a", title: "A-sug")], hasMore: false),
            CancellationToken.None);

        var result = await store.ActivateAsync(second, CancellationToken.None);

        Assert.Equal(1, result.UniqueTitles);
        Assert.Equal(1, result.DuplicatesSuppressed);
        Assert.Single(result.Warnings, warning => warning.Contains("rating drift"));
        Assert.Empty(result.CleanupWarnings);

        var active = await store.TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("A-sug", active.Value.Items[0].Title);
        Assert.Equal(1, active.Value.Manifest.UniqueTitles);
    }

    [Fact]
    public async Task DriftWarningsAreCappedWithSummary()
    {
        var store = Store();
        var first = Enumerable.Range(0, 150).Select(number => Item("d" + number)).ToArray();
        var second = Enumerable.Range(0, 150).Select(number => Item("d" + number, title: "New " + number)).ToArray();
        await store.AppendPageAsync(Partition("safe"), 0, Page(1, first, hasMore: false), CancellationToken.None);
        var checkpoint = await store.AppendPageAsync(
            Partition("suggestive"), 1, Page(1, second, hasMore: false), CancellationToken.None);

        var result = await store.ActivateAsync(checkpoint, CancellationToken.None);

        Assert.Equal(150, result.UniqueTitles);
        Assert.Equal(101, result.Warnings.Count);
        Assert.EndsWith("more rating drifts.", result.Warnings[^1]);
    }

    [Fact]
    public async Task PartitionMarkerResumesAtNextBoundary()
    {
        var store = Store();
        var first = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a")], hasMore: true), CancellationToken.None);

        var marker = await store.CompletePartitionAsync(0, "safe", CancellationToken.None);

        Assert.Equal(1, marker.PartitionIndex);
        Assert.Equal(0, marker.Page);
        Assert.Equal(first.StagingSnapshotId, marker.StagingSnapshotId);
        Assert.Equal(1, marker.StagedRecords);

        var next = await store.AppendPageAsync(
            Partition("suggestive"), 1, Page(1, [Item("b")], hasMore: false),
            CancellationToken.None);
        Assert.Equal(2, next.StagedRecords);

        var result = await store.ActivateAsync(next, CancellationToken.None);
        Assert.Equal(2, result.UniqueTitles);
    }

    [Fact]
    public async Task SeedStagesBaselineForRefreshWithoutDrift()
    {
        var store = Store();
        var first = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a", title: "Old"), Item("b")], hasMore: false),
            CancellationToken.None);
        await store.ActivateAsync(first, CancellationToken.None);

        var (seed, seededKeys) = await store.SeedStagingFromActiveAsync(CancellationToken.None);

        Assert.True(seed.IsRefresh);
        Assert.Equal(-1, seed.PartitionIndex);
        Assert.Equal(2, seed.StagedRecords);
        Assert.Equal(["comix\0a", "comix\0b"], seededKeys.OrderBy(key => key).ToArray());

        var checkpoint = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a", title: "New")], hasMore: false),
            CancellationToken.None, isRefresh: true);
        var result = await store.ActivateAsync(checkpoint, CancellationToken.None, isRefresh: true);

        Assert.Equal(2, result.UniqueTitles);
        Assert.Empty(result.Warnings);
        var active = await store.TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("New", active.Value.Items.First(item => item.TitleId == "a").Title);
        Assert.NotNull(active.Value.Manifest.LastDeltaUtc);
    }

    [Fact]
    public async Task SamePartitionDuplicateKeepsLastWithoutDriftWarning()
    {
        var store = Store();
        await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a", title: "Old")], hasMore: true), CancellationToken.None);
        var checkpoint = await store.AppendPageAsync(
            Partition("safe"), 0, Page(2, [Item("a", title: "New"), Item("b")], hasMore: false),
            CancellationToken.None);

        var result = await store.ActivateAsync(checkpoint, CancellationToken.None);

        Assert.Equal(2, result.UniqueTitles);
        Assert.Equal(1, result.DuplicatesSuppressed);
        Assert.Empty(result.Warnings);

        var active = await store.TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("New", active.Value.Items.First(item => item.TitleId == "a").Title);
    }

    [Fact]
    public async Task PlaceholderTitlesAreCountedAndReported()
    {
        var store = Store();
        var placeholder = Item("a") with
        {
            Title = CatalogSnapshotTitles.PlaceholderFor("h-a"),
            IsTitlePlaceholder = true,
        };
        var checkpoint = await store.AppendPageAsync(
            Partition("erotica"), 0, Page(1, [placeholder, Item("b")], hasMore: false),
            CancellationToken.None);

        var result = await store.ActivateAsync(checkpoint, CancellationToken.None);

        Assert.Equal(2, result.UniqueTitles);
        Assert.Equal(1, result.PlaceholderTitles);

        var active = await store.TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(1, active.Value.Manifest.PlaceholderTitles);
    }

    [Fact]
    public async Task StaleActivationPreservesPreviousSnapshot()
    {
        var store = Store();
        var first = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a")], hasMore: false), CancellationToken.None);
        var activeId = (await store.ActivateAsync(first, CancellationToken.None)).SnapshotId;

        // Activation retires the staging run: continuing it behaves like a
        // fresh empty staging, and the old checkpoint stays stale.
        await Assert.ThrowsAsync<CatalogSnapshotException>(() => store.AppendPageAsync(
            Partition("safe"), 0, Page(2, [Item("b")], hasMore: false), CancellationToken.None));
        await Assert.ThrowsAsync<CatalogSnapshotException>(
            () => store.ActivateAsync(first, CancellationToken.None));

        var manifest = await store.TryLoadManifestAsync();
        Assert.NotNull(manifest);
        Assert.Equal(activeId, manifest.ActiveSnapshotId);

        var active = await store.TryLoadActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("a", active.Value.Items[0].TitleId);
    }

    [Fact]
    public async Task RetentionKeepsActivePlusOnePrevious()
    {
        var store = Store();
        var ids = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            await store.ClearStagingAsync();
            var checkpoint = await store.AppendPageAsync(
                Partition("safe"), 0, Page(1, [Item("t" + round)], hasMore: false),
                CancellationToken.None);
            ids.Add((await store.ActivateAsync(checkpoint, CancellationToken.None)).SnapshotId);
        }

        var remaining = GenerationIds();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(ids[2], remaining);
        Assert.Contains(ids[1], remaining);

        var manifest = await store.TryLoadManifestAsync();
        Assert.NotNull(manifest);
        Assert.Equal(ids[2], manifest.ActiveSnapshotId);
    }

    [Fact]
    public async Task NewerSchemaIsPreservedAndReported()
    {
        var paths = new CatalogMirrorPaths(_root);
        var store = Store();
        var checkpoint = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a")], hasMore: false), CancellationToken.None);
        await store.ActivateAsync(checkpoint, CancellationToken.None);

        using (var bump = new CatalogMirrorDatabase(paths).Open())
        using (var command = bump.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }

        var exception = await Assert.ThrowsAsync<CatalogSnapshotException>(
            () => Store().TryLoadManifestAsync());
        Assert.Contains("newer", exception.Message);

        using (var check = new CatalogMirrorDatabase(paths).Open())
        using (var version = check.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(99L, (long)version.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task CorruptDatabaseIsLoudWithBytesPreserved()
    {
        Directory.CreateDirectory(_root);
        var path = new CatalogMirrorPaths(_root).DatabasePath;
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => Store().TryLoadManifestAsync());
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StaleCheckpointRejectedAfterClear()
    {
        var store = Store();
        var first = await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("a")], hasMore: false), CancellationToken.None);

        await store.ClearStagingAsync();
        await store.AppendPageAsync(
            Partition("safe"), 0, Page(1, [Item("b")], hasMore: false), CancellationToken.None);

        await Assert.ThrowsAsync<CatalogSnapshotException>(
            () => store.ActivateAsync(first, CancellationToken.None));

        var checkpoint = await store.LoadCheckpointAsync();
        Assert.NotNull(checkpoint);
        var second = await store.ActivateAsync(checkpoint, CancellationToken.None);
        Assert.Equal(checkpoint.StagingSnapshotId, second.SnapshotId);
    }

    [Fact]
    public async Task FreshRootLoadsNull()
    {
        var store = Store();
        Assert.Null(await store.TryLoadManifestAsync());
        Assert.Null(await store.TryLoadActiveAsync());
        Assert.Null(await store.LoadCheckpointAsync());
    }

    [Fact]
    public void PathsRejectEscapesAndValidateIds()
    {
        var paths = new CatalogMirrorPaths(_root);

        Assert.Throws<CatalogSnapshotException>(() => paths.EnsureUnderRoot("C:\\Windows\\Temp\\x.json"));
        Assert.Throws<CatalogSnapshotException>(() => CatalogMirrorPaths.ValidateSnapshotId("../evil"));
        Assert.Throws<CatalogSnapshotException>(() => CatalogMirrorPaths.ValidateSnapshotId("has space"));

        var first = CatalogMirrorPaths.HashIdentity("comix", "abc");
        Assert.Equal(first, CatalogMirrorPaths.HashIdentity("comix", "abc"));
        Assert.NotEqual(first, CatalogMirrorPaths.HashIdentity("comix", "abd"));
        Assert.Equal(64, first.Length);

        var id = CatalogMirrorPaths.NewSnapshotId();
        Assert.Equal(id, CatalogMirrorPaths.ValidateSnapshotId(id));
    }

    private HashSet<string> GenerationIds()
    {
        var database = new CatalogMirrorDatabase(new CatalogMirrorPaths(_root));
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation_id FROM catalog_generation;";
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private CatalogSnapshotStore Store() => new(new CatalogMirrorPaths(_root));

    private static CatalogSnapshotPartition Partition(string key) => new(key, key);

    private static CatalogSnapshotPage Page(
        int page, CatalogSnapshotItem[] items, bool hasMore, long? total = 100) =>
        new(items, total, page, hasMore);

    private static CatalogSnapshotItem Item(string id, string? title = null) => new(
        SourceId: "comix",
        TitleId: id,
        TitleHid: "h-" + id,
        CanonicalUrl: "https://comix.ws/title/h-" + id,
        Title: title ?? "Title " + id,
        AlternateTitles: [],
        CoverUrl: null,
        LatestChapterValue: 1,
        LatestChapterLabel: "Ch. 1",
        Rating: "safe",
        Type: "manga",
        Status: "releasing",
        Language: "en",
        Year: 2020,
        Synopsis: null,
        CapturedAtUtc: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        IsTitlePlaceholder: false);
}
