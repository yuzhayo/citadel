using System.IO;
using System.IO.Compression;
using Module.Mangareader.Library;

namespace Module.Mangareader.Library.Tests;

public sealed class LibraryIndexCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-coordinator-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _library;

    public LibraryIndexCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        _library = Path.Combine(_root, "manga");
        Directory.CreateDirectory(_library);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }

    private LibraryIndexCoordinator CreateCoordinator() =>
        new(new LibraryIndexStore(_root), new LibraryTitleLoader());

    private string NewTitle(string name, params string[] chapters)
    {
        var folder = Path.Combine(_library, name);
        Directory.CreateDirectory(folder);
        foreach (var chapter in chapters)
        {
            CreateCbz(folder, chapter);
        }

        return folder;
    }

    private static void CreateCbz(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("page1.jpg");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("not a real image, but a real zip signature");
    }

    [Fact]
    public void ReloadLoadsStoredDocument()
    {
        NewTitle("one-piece", "ch1.cbz");
        var coordinator = CreateCoordinator();
        coordinator.Reconcile(_library);

        var fresh = CreateCoordinator();
        Assert.Empty(fresh.Entries);
        fresh.Reload(_library);

        Assert.Equal(_library, fresh.Root);
        var entry = Assert.Single(fresh.Entries);
        Assert.Equal("one-piece", entry.TitleFolderName);
    }

    [Fact]
    public void ReloadInvalidRootClearsIndex()
    {
        var coordinator = CreateCoordinator();
        coordinator.Reload(_library);
        coordinator.Reload("  ");

        Assert.Null(coordinator.Root);
        Assert.Empty(coordinator.Entries);
    }

    [Fact]
    public void ReconcileAddsNewFoldersWithCreationAddedUtc()
    {
        NewTitle("berserk", "ch1.cbz", "ch2.cbz");
        var coordinator = CreateCoordinator();

        var result = coordinator.Reconcile(_library);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Total);
        var entry = Assert.Single(coordinator.Entries);
        Assert.Equal(2, entry.ChapterCount);
        Assert.Equal(
            Directory.GetCreationTimeUtc(Path.Combine(_library, "berserk")),
            entry.AddedUtc);
    }

    [Fact]
    public void ReconcilePreservesAddedUtcAcrossContentChange()
    {
        // Directory timestamps can share one tick for rapid successive writes
        // (observed on this machine), so instead of relying on an implicit
        // mtime bump, a stale fingerprint is planted straight into the stored
        // document: the logic under test is what a moved fingerprint
        // triggers, not filesystem tick behavior.
        var folder = NewTitle("one-piece", "ch1.cbz");
        var seed = new LibraryTitleLoader().BuildEntry(folder);
        Assert.NotNull(seed);
        var pinnedAdded = new DateTime(2020, 5, 5, 5, 5, 5, DateTimeKind.Utc);
        var stale = seed with
        {
            AddedUtc = pinnedAdded,
            FolderFingerprintUtc = DateTime.MinValue,
        };
        Assert.True(new LibraryIndexStore(_root).Save(_library, [stale]).Saved);

        CreateCbz(folder, "ch2.cbz");
        var coordinator = CreateCoordinator();
        var result = coordinator.Reconcile(_library);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        var after = Assert.Single(coordinator.Entries);
        Assert.Equal(pinnedAdded, after.AddedUtc);
        Assert.Equal(2, after.ChapterCount);
        Assert.Equal("ch1", after.FirstChapterLabel);
        Assert.Equal("ch2", after.LatestChapterLabel);
    }

    [Fact]
    public void ReconcileDropsMissingFoldersAndPersistsRemoval()
    {
        var folder = NewTitle("one-piece", "ch1.cbz");
        NewTitle("berserk", "ch1.cbz");
        var coordinator = CreateCoordinator();
        coordinator.Reconcile(_library);
        Assert.Equal(2, coordinator.Entries.Count);

        Directory.Delete(folder, recursive: true);
        var result = coordinator.Reconcile(_library);

        Assert.Equal(1, result.Removed);
        Assert.Equal("berserk", Assert.Single(coordinator.Entries).TitleFolderName);

        var fresh = CreateCoordinator();
        fresh.Reload(_library);
        Assert.Equal("berserk", Assert.Single(fresh.Entries).TitleFolderName);
    }

    [Fact]
    public void MissingRootThrowsAndKeepsPreviousIndex()
    {
        NewTitle("one-piece", "ch1.cbz");
        var coordinator = CreateCoordinator();
        coordinator.Reconcile(_library);

        Assert.Throws<DirectoryNotFoundException>(
            () => coordinator.Reconcile(Path.Combine(_root, "gone")));

        Assert.Equal("one-piece", Assert.Single(coordinator.Entries).TitleFolderName);
    }

    [Fact]
    public void CancelledReconcileKeepsPreviousIndex()
    {
        NewTitle("one-piece", "ch1.cbz");
        var coordinator = CreateCoordinator();
        coordinator.Reconcile(_library);
        NewTitle("berserk", "ch1.cbz");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => coordinator.Reconcile(_library, cancellation.Token));

        Assert.Equal("one-piece", Assert.Single(coordinator.Entries).TitleFolderName);
    }

    [Fact]
    public void UnchangedReconcileDoesNotRewriteTheFile()
    {
        NewTitle("one-piece", "ch1.cbz");
        var coordinator = CreateCoordinator();
        coordinator.Reconcile(_library);
        var storageFile = Assert.Single(Directory.EnumerateFiles(_root));
        var before = File.ReadAllBytes(storageFile);

        var result = coordinator.Reconcile(_library);

        Assert.Equal((0, 0, 0), (result.Added, result.Updated, result.Removed));
        Assert.Equal(before, File.ReadAllBytes(storageFile));
    }

    [Fact]
    public void ReindexOneDropsAGuttedFolder()
    {
        var folder = NewTitle("one-piece", "ch1.cbz");
        var coordinator = CreateCoordinator();
        coordinator.Reconcile(_library);
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);

        var rebuilt = coordinator.ReindexOne(folder);

        Assert.Null(rebuilt);
        Assert.Empty(coordinator.Entries);
    }

    [Fact]
    public void ReconcileFillsThumbnailPathsThroughTheProvider()
    {
        NewTitle("one-piece", "ch1.cbz");
        var covers = new FakeCovers();
        var coordinator = new LibraryIndexCoordinator(new LibraryIndexStore(_root), new LibraryTitleLoader(), covers);

        var result = coordinator.Reconcile(_library);

        Assert.Equal(1, result.Added);
        Assert.Equal("thumb:one-piece", Assert.Single(coordinator.Entries).CoverThumbnailPath);
    }

    [Fact]
    public void RemovalPurgesOnlyTheDroppedThumbnail()
    {
        var folder = NewTitle("one-piece", "ch1.cbz");
        NewTitle("berserk", "ch1.cbz");
        var covers = new FakeCovers();
        var coordinator = new LibraryIndexCoordinator(new LibraryIndexStore(_root), new LibraryTitleLoader(), covers);
        coordinator.Reconcile(_library);

        Directory.Delete(folder, recursive: true);
        coordinator.Reconcile(_library);

        // Every dirty reconcile purges; the last one must keep only the survivor.
        var lastPurge = covers.PurgeCalls[^1];
        Assert.Equal(["thumb:berserk"], lastPurge.OrderBy(path => path));
    }

    [Fact]
    public void ProviderFailureKeepsTheEntry()
    {
        NewTitle("one-piece", "ch1.cbz");
        var coordinator = new LibraryIndexCoordinator(
            new LibraryIndexStore(_root), new LibraryTitleLoader(), new ThrowingCovers());

        var result = coordinator.Reconcile(_library);

        Assert.Equal(1, result.Added);
        Assert.Equal(string.Empty, Assert.Single(coordinator.Entries).CoverThumbnailPath);
    }

    private sealed class FakeCovers : ILibraryCoverThumbnails
    {
        public List<IReadOnlyCollection<string>> PurgeCalls { get; } = new();

        public string? EnsureThumbnail(LibraryIndexEntry entry, CancellationToken cancellationToken) =>
            "thumb:" + entry.TitleFolderName;

        public void PurgeExcept(IReadOnlyCollection<string> keepPaths) => PurgeCalls.Add(keepPaths);
    }

    private sealed class ThrowingCovers : ILibraryCoverThumbnails
    {
        public string? EnsureThumbnail(LibraryIndexEntry entry, CancellationToken cancellationToken) =>
            throw new IOException("cache disk is full");

        public void PurgeExcept(IReadOnlyCollection<string> keepPaths)
        {
        }
    }
}
