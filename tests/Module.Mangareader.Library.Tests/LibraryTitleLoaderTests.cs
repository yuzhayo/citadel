using System.IO;
using System.IO.Compression;
using Module.Mangareader.Library;

namespace Module.Mangareader.Library.Tests;

public sealed class LibraryTitleLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-loader-tests",
        Guid.NewGuid().ToString("N"));

    public LibraryTitleLoaderTests() => Directory.CreateDirectory(_root);

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

    private string NewTitleFolder(string name = "one-piece")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
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
    public void ListChapters_SkipsNonArchivesAndSortsNaturally()
    {
        var folder = NewTitleFolder();
        CreateCbz(folder, "10.cbz");
        CreateCbz(folder, "2.cbz");
        CreateCbz(folder, "a.cbz");
        File.WriteAllText(Path.Combine(folder, "note.txt"), "not a chapter");
        File.WriteAllText(Path.Combine(folder, "fake.cbz"), "zip extension, garbage bytes");

        var chapters = new LibraryTitleLoader().ListChapters(folder);

        Assert.Equal(["2", "10", "a"], chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public void LoadTitle_BuildsFullTitleWithFolderCreationAddedUtc()
    {
        var folder = NewTitleFolder();
        CreateCbz(folder, "ch1.cbz");
        CreateCbz(folder, "ch2.cbz");

        var title = new LibraryTitleLoader().LoadTitle(folder);

        Assert.NotNull(title);
        Assert.Equal("one-piece", title.Title);
        Assert.Equal(2, title.ChapterCount);
        Assert.Equal(Directory.GetCreationTimeUtc(folder), title.AddedUtc);
    }

    [Fact]
    public void LoadTitle_RespectsAddedUtcOverride()
    {
        var folder = NewTitleFolder();
        CreateCbz(folder, "ch1.cbz");
        var pinned = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var title = new LibraryTitleLoader().LoadTitle(folder, pinned);

        Assert.NotNull(title);
        Assert.Equal(pinned, title.AddedUtc);
    }

    [Fact]
    public void LoadTitle_MissingOrEmptyFolderReturnsNull()
    {
        var loader = new LibraryTitleLoader();

        Assert.Null(loader.LoadTitle(Path.Combine(_root, "gone")));
        Assert.Null(loader.LoadTitle(NewTitleFolder("empty")));
    }

    [Fact]
    public void BuildEntry_NullWhenNoChapters()
    {
        Assert.Null(new LibraryTitleLoader().BuildEntry(NewTitleFolder("empty")));
    }

    [Fact]
    public void BuildEntry_PreservesAddedUtcFromExistingEntry()
    {
        var folder = NewTitleFolder();
        CreateCbz(folder, "ch1.cbz");
        var loader = new LibraryTitleLoader();

        var first = loader.BuildEntry(folder);
        Assert.NotNull(first);

        CreateCbz(folder, "ch2.cbz");
        var second = loader.BuildEntry(folder, first);
        Assert.NotNull(second);

        Assert.Equal(first.AddedUtc, second.AddedUtc);
        Assert.Equal(2, second.ChapterCount);
        Assert.Equal("ch1", second.FirstChapterLabel);
        Assert.Equal("ch2", second.LatestChapterLabel);
    }

    [Fact]
    public void BuildEntry_CoverFingerprintSelectsNaturalFirstArchiveRegardlessOfMtime()
    {
        var folder = NewTitleFolder();
        CreateCbz(folder, "ch10.cbz");
        CreateCbz(folder, "ch2.cbz");
        CreateCbz(folder, "ch1.cbz");
        // Scramble mtimes: newest file must still lose to natural order.
        File.SetLastWriteTimeUtc(Path.Combine(folder, "ch10.cbz"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "ch2.cbz"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "ch1.cbz"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var loader = new LibraryTitleLoader();
        var first = loader.BuildEntry(folder);
        var second = loader.BuildEntry(folder);
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.StartsWith("archive:ch1.cbz#", first.CoverFingerprint, StringComparison.Ordinal);
        Assert.Equal(first.CoverFingerprint, second.CoverFingerprint);
        Assert.Equal(string.Empty, first.CoverThumbnailPath);
    }

    [Fact]
    public void BuildEntry_PrefersCoverFileOverArchives()
    {
        var folder = NewTitleFolder();
        CreateCbz(folder, "ch1.cbz");
        File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "cover.png"), [4, 5, 6]);

        var entry = new LibraryTitleLoader().BuildEntry(folder);

        Assert.NotNull(entry);
        Assert.StartsWith("cover:cover.png#", entry.CoverFingerprint, StringComparison.Ordinal);
    }
}
