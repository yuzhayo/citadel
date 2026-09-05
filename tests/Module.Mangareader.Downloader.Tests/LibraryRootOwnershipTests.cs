using System.IO;
using Module.Mangareader.Library;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Library root ownership. One context restores and commits the root; a failed
/// or cancelled scan changes neither the in-memory root nor storage, which is
/// what lets the Downloader snapshot a target without owning any root policy.
/// </summary>
public sealed class LibraryRootOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.LibraryRoot.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _storePath;

    public LibraryRootOwnershipTests()
    {
        Directory.CreateDirectory(_root);
        _storePath = Path.Combine(_root, "library-path.txt");
    }

    private LibraryRootContext CreateContext() =>
        new(new LibraryPathStore(_storePath));

    [Fact]
    public void RestoreAdoptsThePersistedRootAndNotifiesOnlyOnChange()
    {
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(library);
        new LibraryPathStore(_storePath).Save(library);

        var context = CreateContext();
        var changes = 0;
        context.RootChanged += (_, _) => changes++;

        var loaded = context.Restore();

        Assert.Null(loaded.Warning);
        Assert.Equal(library, context.CurrentRoot);
        Assert.Equal(1, changes);

        // A second restore of the same value is not a change.
        context.Restore();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void RestoreOfAnEmptyStoreLeavesTheRootUnavailable()
    {
        var context = CreateContext();

        var loaded = context.Restore();

        Assert.Null(loaded.Path);
        Assert.Null(context.CurrentRoot);
    }

    [Fact]
    public void SuccessfulScanCommitsTheCapturedPathAndPersistsIt()
    {
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(library);
        var context = CreateContext();

        var attempt = context.BeginScan(library);
        var save = context.CompleteScan(attempt, succeeded: true, cancelled: false);

        Assert.True(save.Saved);
        Assert.Equal(library, context.CurrentRoot);
        Assert.Equal(library, new LibraryPathStore(_storePath).Load().Path);
    }

    [Fact]
    public void FailedOrCancelledScanChangesNeitherRootNorStorage()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var context = CreateContext();
        context.CompleteScan(context.BeginScan(first), succeeded: true, cancelled: false);

        var failed = context.CompleteScan(context.BeginScan(second), succeeded: false, cancelled: false);
        var cancelled = context.CompleteScan(context.BeginScan(second), succeeded: true, cancelled: true);

        Assert.False(failed.Saved);
        Assert.False(cancelled.Saved);
        Assert.Equal(first, context.CurrentRoot);
        Assert.Equal(first, new LibraryPathStore(_storePath).Load().Path);
    }

    [Fact]
    public void CapturedPathIsPersistedNotWhateverTheFieldSaysLater()
    {
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(library);
        var context = CreateContext();

        // The attempt captures the path at scan start; completion never re-reads
        // a field, so a later edit cannot leak into what is saved.
        var attempt = context.BeginScan(library);
        context.CompleteScan(attempt, succeeded: true, cancelled: false);

        Assert.Equal(library, context.CurrentRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
