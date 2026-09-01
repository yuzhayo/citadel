using System.IO;
using Module.Mangareader.Library;

namespace Module.Mangareader.Library.Tests;

/// <summary>
/// The coordination rule between a scan attempt and persistence, exercised
/// through the pure seam instead of fragile assertions on view source text.
/// </summary>
public sealed class LibraryScanPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-scan-tests",
        Guid.NewGuid().ToString("N"));

    private readonly LibraryPathStore _store;
    private readonly LibraryScanPersistence _persistence;

    public LibraryScanPersistenceTests()
    {
        Directory.CreateDirectory(_root);
        _store = new LibraryPathStore(Path.Combine(_root, "library-path.txt"));
        _persistence = new LibraryScanPersistence(_store);
    }

    [Fact]
    public void SuccessfulScanPersistsTheScannedPath()
    {
        var folder = Path.Combine(_root, "manga");
        Directory.CreateDirectory(folder);
        var attempt = _persistence.BeginScan(folder);

        var result = _persistence.CompleteScan(attempt, succeeded: true, cancelled: false);

        Assert.True(result.Saved);
        Assert.Equal(folder, _store.Load().Path);
    }

    [Fact]
    public void CompletionPersistsTheCapturedPathEvenIfTheFieldChanged()
    {
        // Audit regression: scan of folder A starts, the field is edited to
        // folder B mid-scan, then scan A completes. The attempt captured at
        // start is the only value completion can persist — B never enters
        // the seam, so A is what survives.
        var folderA = Path.Combine(_root, "folder-a");
        Directory.CreateDirectory(folderA);
        var folderB = Path.Combine(_root, "folder-b");
        Directory.CreateDirectory(folderB);

        var attempt = _persistence.BeginScan(folderA);
        var result = _persistence.CompleteScan(attempt, succeeded: true, cancelled: false);

        Assert.True(result.Saved);
        Assert.Equal(folderA, _store.Load().Path);
        Assert.NotEqual(folderB, _store.Load().Path);
    }

    [Fact]
    public void BeginScanCapturesTrimmedFieldText()
    {
        var folder = Path.Combine(_root, "manga");
        var attempt = _persistence.BeginScan($"  {folder}  ");

        Assert.Equal(folder, attempt.CapturedPath);
    }

    [Fact]
    public void SuccessfulZeroTitleScanRemainsEligibleForPersistence()
    {
        // A valid folder with zero manga titles is still a successful scan.
        var emptyLibrary = Path.Combine(_root, "empty-library");
        Directory.CreateDirectory(emptyLibrary);
        Assert.Empty(Directory.GetDirectories(emptyLibrary));
        var attempt = _persistence.BeginScan(emptyLibrary);

        var result = _persistence.CompleteScan(attempt, succeeded: true, cancelled: false);

        Assert.True(result.Saved);
        Assert.Equal(emptyLibrary, _store.Load().Path);
    }

    [Fact]
    public void FailedScanDoesNotOverwriteThePreviousSavedPath()
    {
        var previous = Path.Combine(_root, "previous");
        _store.Save(previous);
        var attempt = _persistence.BeginScan(Path.Combine(_root, "broken"));

        var result = _persistence.CompleteScan(attempt, succeeded: false, cancelled: false);

        Assert.False(result.Saved);
        Assert.Null(result.Warning);
        Assert.Equal(previous, _store.Load().Path);
    }

    [Fact]
    public void CancelledScanDoesNotOverwriteThePreviousSavedPath()
    {
        var previous = Path.Combine(_root, "previous");
        _store.Save(previous);
        var attempt = _persistence.BeginScan(Path.Combine(_root, "interrupted"));

        var result = _persistence.CompleteScan(attempt, succeeded: true, cancelled: true);

        Assert.False(result.Saved);
        Assert.Null(result.Warning);
        Assert.Equal(previous, _store.Load().Path);
    }

    [Fact]
    public void SaveFailureDuringSuccessfulScanSurfacesStructuredWarning()
    {
        var attempt = _persistence.BeginScan("relative/folder");

        var result = _persistence.CompleteScan(attempt, succeeded: true, cancelled: false);

        Assert.False(result.Saved);
        Assert.NotNull(result.Warning);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
