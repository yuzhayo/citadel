using System.IO;
using Module.Mangareader.Library;

namespace Module.Mangareader.Library.Tests;

public sealed class LibraryIndexWatcherTests : IDisposable
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(750);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-watcher-tests",
        Guid.NewGuid().ToString("N"));

    public LibraryIndexWatcherTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void ResolveTitleFolder_MapsNestedPathsToFirstLevel()
    {
        var root = Path.Combine(_root, "manga");

        Assert.Equal(
            Path.Combine(root, "one-piece"),
            LibraryIndexWatcher.ResolveTitleFolder(
                root, Path.Combine(root, "one-piece", "ch1.cbz")));
        Assert.Equal(
            Path.Combine(root, "one-piece"),
            LibraryIndexWatcher.ResolveTitleFolder(
                root, Path.Combine(root, "one-piece")));
    }

    [Fact]
    public void ResolveTitleFolder_RejectsRootItselfOutsideAndGarbage()
    {
        var root = Path.Combine(_root, "manga");

        Assert.Null(LibraryIndexWatcher.ResolveTitleFolder(root, root));
        Assert.Null(LibraryIndexWatcher.ResolveTitleFolder(
            root, Path.Combine(_root, "other", "x.cbz")));
        Assert.Null(LibraryIndexWatcher.ResolveTitleFolder(root, ""));
    }

    [Fact]
    public void RecordCoalescesBurstIntoOneChange()
    {
        using var watcher = new LibraryIndexWatcher(_root);
        var file = Path.Combine(_root, "one-piece", "ch1.cbz");

        watcher.Record(file, removed: false);
        watcher.Record(file, removed: false);
        watcher.Record(file, removed: false);

        var due = watcher.CollectDue(DateTime.UtcNow + Period);

        var single = Assert.Single(due);
        Assert.Equal(Path.Combine(_root, "one-piece"), single.FolderPath);
        Assert.False(single.Removed);
        Assert.Empty(watcher.CollectDue(DateTime.UtcNow + Period));
    }

    [Fact]
    public void RecordRemovalWinsOverQueuedChange()
    {
        using var watcher = new LibraryIndexWatcher(_root);
        var file = Path.Combine(_root, "one-piece", "ch1.cbz");

        watcher.Record(file, removed: false);
        watcher.Record(Path.Combine(_root, "one-piece"), removed: true);

        var due = watcher.CollectDue(DateTime.UtcNow + Period);

        var single = Assert.Single(due);
        Assert.True(single.Removed);
    }

    [Fact]
    public void RecordChangeAfterRemovalReportsChange()
    {
        using var watcher = new LibraryIndexWatcher(_root);
        var folder = Path.Combine(_root, "one-piece");

        watcher.Record(folder, removed: true);
        watcher.Record(Path.Combine(folder, "ch1.cbz"), removed: false);

        var due = watcher.CollectDue(DateTime.UtcNow + Period);

        var single = Assert.Single(due);
        Assert.Equal(folder, single.FolderPath);
        Assert.False(single.Removed);
    }

    [Fact]
    public void CollectDueRespectsTheQuietPeriod()
    {
        using var watcher = new LibraryIndexWatcher(_root);
        watcher.Record(Path.Combine(_root, "one-piece", "ch1.cbz"), removed: false);

        Assert.Empty(watcher.CollectDue(DateTime.UtcNow));
        Assert.Single(watcher.CollectDue(DateTime.UtcNow + Period));
    }
}
