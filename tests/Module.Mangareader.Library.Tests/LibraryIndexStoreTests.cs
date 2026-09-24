using System.IO;
using Module.Mangareader.Library;

namespace Module.Mangareader.Library.Tests;

public sealed class LibraryIndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-index-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _libraryA;
    private readonly string _libraryB;

    public LibraryIndexStoreTests()
    {
        Directory.CreateDirectory(_root);
        _libraryA = Path.Combine(_root, "manga-a");
        _libraryB = Path.Combine(_root, "manga-b");
        Directory.CreateDirectory(_libraryA);
        Directory.CreateDirectory(_libraryB);
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

    private LibraryIndexStore CreateStore(ILibraryPathFileIO? fileIO = null) =>
        new(_root, fileIO);

    private static LibraryIndexEntry Entry(
        string folderName,
        string? folderPath = null,
        int chapters = 12) =>
        new(
            folderName,
            folderPath ?? Path.Combine("C:", "manga", folderName),
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            chapters,
            "Ch.1",
            "Ch.12",
            new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            "cover.jpg#42",
            Path.Combine("C:", "cache", folderName + ".jpg"));

    [Fact]
    public void MissingIndexReturnsEmptyWithoutWarning()
    {
        var result = CreateStore().Load(_libraryA);

        Assert.Null(result.LibraryRoot);
        Assert.Empty(result.Entries);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void RoundTripPreservesRootEntriesAndTimestamp()
    {
        var store = CreateStore();
        var entries = new[] { Entry("one-piece"), Entry("berserk", chapters: 364) };

        var save = store.Save(_libraryA, entries);
        var load = store.Load(_libraryA);

        Assert.True(save.Saved);
        Assert.Null(save.Warning);
        Assert.Equal(_libraryA, load.LibraryRoot);
        Assert.Null(load.Warning);
        Assert.Equal(entries, load.Entries);
        Assert.True(load.UpdatedUtc > DateTime.MinValue);
    }

    [Fact]
    public void MalformedContentIsIgnoredAndFileIsKept()
    {
        var store = CreateStore();
        store.Save(_libraryA, [Entry("one-piece")]);
        var storageFile = Assert.Single(Directory.EnumerateFiles(_root));
        File.WriteAllText(storageFile, "{ not json");

        var result = store.Load(_libraryA);

        Assert.Empty(result.Entries);
        Assert.NotNull(result.Warning);
        Assert.True(File.Exists(storageFile));
    }

    [Fact]
    public void UnknownSchemaVersionIsIgnoredAndFileIsKept()
    {
        var store = CreateStore();
        store.Save(_libraryA, [Entry("one-piece")]);
        var storageFile = Assert.Single(Directory.EnumerateFiles(_root));
        var content = File.ReadAllText(storageFile).Replace("\"Version\": 1", "\"Version\": 999");
        File.WriteAllText(storageFile, content);

        var result = store.Load(_libraryA);

        Assert.Empty(result.Entries);
        Assert.NotNull(result.Warning);
        Assert.True(File.Exists(storageFile));
    }

    [Fact]
    public void OtherRootHasNoIndexOfItsOwn()
    {
        var store = CreateStore();
        store.Save(_libraryA, [Entry("one-piece")]);

        // Root B never had an index: an ordinary empty result, not a warning.
        // This proves one root can never read another root's document.
        var result = store.Load(_libraryB);

        Assert.Null(result.LibraryRoot);
        Assert.Empty(result.Entries);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void MismatchedRootDocumentIsIgnoredAndFileIsKept()
    {
        var store = CreateStore();
        store.Save(_libraryA, [Entry("one-piece")]);
        var sourceFile = Assert.Single(Directory.EnumerateFiles(_root));

        // Simulate a hand-edited or collided file: valid document for root A
        // sitting under root B's file name.
        var rootB = LibraryPathStore.Normalize(_libraryB)!;
        var targetFile = Path.Combine(_root, LibraryIndexStore.FileNameForRoot(rootB));
        File.Copy(sourceFile, targetFile);

        var result = store.Load(_libraryB);

        Assert.Empty(result.Entries);
        Assert.NotNull(result.Warning);
        Assert.True(File.Exists(targetFile));
    }

    [Fact]
    public void FailedSaveKeepsPreviousIndex()
    {
        var store = CreateStore();
        var first = new[] { Entry("one-piece") };
        Assert.True(store.Save(_libraryA, first).Saved);

        var failing = CreateStore(new ThrowingWriteFileIO());
        var save = failing.Save(_libraryA, [Entry("berserk")]);

        Assert.False(save.Saved);
        Assert.NotNull(save.Warning);
        Assert.Equal(first, CreateStore().Load(_libraryA).Entries);
    }

    [Fact]
    public void OversizedContentIsRejectedSafely()
    {
        var store = CreateStore();
        store.Save(_libraryA, [Entry("one-piece")]);
        var storageFile = Assert.Single(Directory.EnumerateFiles(_root));
        using (var stream = File.OpenWrite(storageFile))
        {
            stream.SetLength(LibraryIndexStore.MaximumContentBytes + 16);
        }

        var result = store.Load(_libraryA);

        Assert.Empty(result.Entries);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void DuplicateAndBlankEntriesAreSanitized()
    {
        var store = CreateStore();
        var keep = Entry("one-piece");
        var entries = new[]
        {
            keep,
            Entry("ONE-PIECE-DIFFERENT-NAME", keep.FolderPath),
            new LibraryIndexEntry("", Path.Combine("C:", "manga", "blank"), keep.AddedUtc, 3, "a", "b", keep.FolderFingerprintUtc, "", ""),
            new LibraryIndexEntry("nofolder", "   ", keep.AddedUtc, 3, "a", "b", keep.FolderFingerprintUtc, "", ""),
            Entry("negative", chapters: -5),
        };

        store.Save(_libraryA, entries);
        var loaded = store.Load(_libraryA).Entries;

        Assert.Equal(2, loaded.Count);
        Assert.Equal(keep, loaded[0]);
        Assert.Equal(0, loaded[1].ChapterCount);
    }

    [Fact]
    public void SanitizeDropsNonRootedAndMalformedPaths()
    {
        var valid = Entry("one-piece");
        var entries = new[]
        {
            valid,
            Entry("relative", Path.Combine("manga", "relative")),
            // Embedded NUL is rejected by GetFullPath on every runtime.
            new LibraryIndexEntry(
                "badchars", "C:\\manga\\bad\0name", valid.AddedUtc, 1,
                "a", "b", valid.FolderFingerprintUtc, "", ""),
        };

        var store = CreateStore();
        store.Save(_libraryA, entries);
        var loaded = store.Load(_libraryA).Entries;

        Assert.Equal(valid, Assert.Single(loaded));
    }

    [Fact]
    public void SanitizeCanonicalizesFolderPaths()
    {
        var folder = Path.Combine(_libraryA, "sub", "..", "one-piece");
        var entry = Entry("one-piece", folder);
        var store = CreateStore();

        store.Save(_libraryA, [entry]);
        var loaded = store.Load(_libraryA).Entries;

        Assert.Equal(Path.Combine(_libraryA, "one-piece"), Assert.Single(loaded).FolderPath);
    }

    [Fact]
    public void AtomicSaveLeavesNoTempFile()
    {
        CreateStore().Save(_libraryA, [Entry("one-piece")]);

        var files = Directory.EnumerateFiles(_root).ToArray();
        var leftover = Assert.Single(files);
        Assert.EndsWith(".library-index.json", leftover, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRootSaveLeavesNothingBehind()
    {
        var store = CreateStore();

        var save = store.Save("  ", [Entry("one-piece")]);

        Assert.False(save.Saved);
        Assert.NotNull(save.Warning);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public void InvalidRootLoadReportsWarning()
    {
        var result = CreateStore().Load("relative/manga");

        Assert.Empty(result.Entries);
        Assert.NotNull(result.Warning);
    }

    private sealed class ThrowingWriteFileIO : ILibraryPathFileIO
    {
        public string ReadAllText(string path) => File.ReadAllText(path);

        public void WriteAllText(string path, string content) =>
            throw new IOException("disk is full");

        public void Move(string sourcePath, string destinationPath, bool overwrite) =>
            File.Move(sourcePath, destinationPath, overwrite);
    }
}
