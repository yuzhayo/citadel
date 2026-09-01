using System.IO;
using Module.Mangareader.Library;

namespace Module.Mangareader.Library.Tests;

public sealed class LibraryPathStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-path-tests",
        Guid.NewGuid().ToString("N"));

    private string StoragePath => Path.Combine(_root, "library-path.txt");

    public LibraryPathStoreTests() => Directory.CreateDirectory(_root);

    private LibraryPathStore CreateStore(ILibraryPathFileIO? fileIO = null) =>
        new(StoragePath, fileIO);

    [Fact]
    public void MissingStorageFileReturnsNoPathAndNoWarning()
    {
        var result = CreateStore().Load();

        Assert.Null(result.Path);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void ValidAbsolutePathRoundTrips()
    {
        var store = CreateStore();
        var folder = Path.Combine(_root, "manga");
        Directory.CreateDirectory(folder);

        var save = store.Save(folder);
        var load = store.Load();

        Assert.True(save.Saved);
        Assert.Null(save.Warning);
        Assert.Equal(folder, load.Path);
        Assert.Null(load.Warning);
    }

    [Fact]
    public void PathIsNormalizedBeforeStorage()
    {
        var store = CreateStore();
        // Trailing separator plus a redundant relative segment.
        var raw = Path.Combine(_root, "sub") + Path.DirectorySeparatorChar
            + ".." + Path.DirectorySeparatorChar
            + "manga" + Path.DirectorySeparatorChar;
        var expected = Path.Combine(_root, "manga");

        store.Save(raw);

        Assert.Equal(expected, File.ReadAllText(StoragePath).Trim());
        Assert.Equal(expected, store.Load().Path);
    }

    [Fact]
    public void SaveReplacesThePreviousPath()
    {
        var store = CreateStore();
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");

        store.Save(first);
        store.Save(second);

        Assert.Equal(second, File.ReadAllText(StoragePath).Trim());
        Assert.Equal(second, store.Load().Path);
    }

    [Fact]
    public void AtomicSaveLeavesNoTempFile()
    {
        var store = CreateStore();
        store.Save(Path.Combine(_root, "manga"));

        var files = Directory.EnumerateFiles(_root).ToArray();
        var leftover = Assert.Single(files);
        Assert.Equal(StoragePath, leftover);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void EmptyOrWhitespaceContentIsRejectedSafely(string content)
    {
        File.WriteAllText(StoragePath, content);

        var result = CreateStore().Load();

        Assert.Null(result.Path);
        Assert.NotNull(result.Warning);
    }

    [Theory]
    [InlineData("relative/manga")]
    [InlineData("not a path at all <>|")]
    public void MalformedOrNonAbsoluteContentIsRejectedSafely(string content)
    {
        File.WriteAllText(StoragePath, content);

        var result = CreateStore().Load();

        Assert.Null(result.Path);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void OversizedContentIsRejectedSafely()
    {
        File.WriteAllText(
            StoragePath,
            new string('C', LibraryPathStore.MaximumContentBytes + 16));

        var result = CreateStore().Load();

        Assert.Null(result.Path);
        Assert.Contains("too large", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void InaccessibleStorageReturnsStructuredWarningWithoutCrashing()
    {
        File.WriteAllText(StoragePath, Path.Combine(_root, "manga"));
        var failingIO = new FakeFileIO
        {
            ReadException = new UnauthorizedAccessException("Denied by test."),
        };

        var result = CreateStore(failingIO).Load();

        Assert.Null(result.Path);
        Assert.NotNull(result.Warning);
        Assert.Contains("could not be read", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableButSyntacticallyValidDirectoryIsRetained()
    {
        var store = CreateStore();
        var unavailable = Path.Combine(_root, "no-such-drive-folder", "manga");
        Assert.False(Directory.Exists(unavailable));
        store.Save(unavailable);

        var result = store.Load();

        Assert.Equal(unavailable, result.Path);
        Assert.Null(result.Warning);
        Assert.Equal(unavailable, File.ReadAllText(StoragePath).Trim());
    }

    [Fact]
    public void FailedSaveDoesNotCorruptThePreviousValue()
    {
        var store = CreateStore();
        var previous = Path.Combine(_root, "previous");
        store.Save(previous);

        var failingIO = new FakeFileIO
        {
            MoveException = new IOException("Disk refused the move in this test."),
        };
        var failed = new LibraryPathStore(StoragePath, failingIO)
            .Save(Path.Combine(_root, "next"));

        Assert.False(failed.Saved);
        Assert.NotNull(failed.Warning);
        Assert.Equal(previous, File.ReadAllText(StoragePath).Trim());
    }

    [Fact]
    public void RejectedSaveInputLeavesThePreviousValueUntouched()
    {
        var store = CreateStore();
        var previous = Path.Combine(_root, "previous");
        store.Save(previous);

        var empty = store.Save("   ");
        var relative = store.Save("relative/folder");

        Assert.False(empty.Saved);
        Assert.False(relative.Saved);
        Assert.NotNull(empty.Warning);
        Assert.NotNull(relative.Warning);
        Assert.Equal(previous, File.ReadAllText(StoragePath).Trim());
    }

    [Fact]
    public void LoadToleratesSurroundingWhitespaceAndBomFreeUtf8()
    {
        var folder = Path.Combine(_root, "manga");
        File.WriteAllText(StoragePath, $"  {folder}\r\n");

        var result = CreateStore().Load();

        Assert.Equal(folder, result.Path);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void SeparateInstancesShareTheSameStorage()
    {
        var folder = Path.Combine(_root, "manga");
        new LibraryPathStore(StoragePath).Save(folder);

        var result = new LibraryPathStore(StoragePath).Load();

        Assert.Equal(folder, result.Path);
    }

    [Fact]
    public void ConcurrentSavesAcrossInstancesNeverLeaveHalfWrittenContent()
    {
        var candidates = Enumerable.Range(0, 8)
            .Select(index => Path.Combine(_root, $"library-{index}"))
            .ToArray();

        Parallel.For(0, 64, index =>
        {
            var store = new LibraryPathStore(StoragePath);
            var result = store.Save(candidates[index % candidates.Length]);
            Assert.True(result.Saved);
        });

        var loaded = new LibraryPathStore(StoragePath).Load();
        Assert.Null(loaded.Warning);
        Assert.Contains(loaded.Path, candidates);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
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

    private sealed class FakeFileIO : ILibraryPathFileIO
    {
        public Exception? ReadException { get; init; }
        public Exception? WriteException { get; init; }
        public Exception? MoveException { get; init; }

        public string ReadAllText(string path)
        {
            if (ReadException is not null) throw ReadException;
            return File.ReadAllText(path);
        }

        public void WriteAllText(string path, string content)
        {
            if (WriteException is not null) throw WriteException;
            File.WriteAllText(path, content);
        }

        public void Move(string sourcePath, string destinationPath, bool overwrite)
        {
            if (MoveException is not null) throw MoveException;
            File.Move(sourcePath, destinationPath, overwrite);
        }
    }
}
