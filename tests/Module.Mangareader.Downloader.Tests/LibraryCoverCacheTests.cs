using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.Library;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Real imaging through the production cache: thumbnails are decoded from a
/// `cover.*` file or from the first archive page, stored under a stable key,
/// and decoded back for cards. Failure paths stay best effort.
/// </summary>
public sealed class LibraryCoverCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.CoverCache.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _library;
    private readonly string _cache;

    public LibraryCoverCacheTests()
    {
        _library = Path.Combine(_root, "library");
        _cache = Path.Combine(_root, "cover-cache");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_cache);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static byte[] PngBytes(int size = 16)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x40;
            pixels[offset + 1] = 0x80;
            pixels[offset + 2] = 0xC0;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteCbz(string path, byte[] pageBytes)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("001.png", CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(pageBytes);
    }

    private string NewTitle(string name)
    {
        var folder = Path.Combine(_library, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void CoverFileBecomesACachedThumbnailThatDecodes()
    {
        var folder = NewTitle("one-piece");
        File.WriteAllBytes(Path.Combine(folder, "cover.png"), PngBytes());
        WriteCbz(Path.Combine(folder, "ch1.cbz"), PngBytes());
        var entry = new LibraryTitleLoader().BuildEntry(folder);
        Assert.NotNull(entry);

        var cache = new LibraryCoverCache(_cache);
        var first = cache.EnsureThumbnail(entry, CancellationToken.None);
        var second = cache.EnsureThumbnail(entry, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.NotNull(LibraryCoverCache.DecodeFile(first));
    }

    [Fact]
    public void FirstArchivePageIsUsedWhenNoCoverFileExists()
    {
        var folder = NewTitle("berserk");
        WriteCbz(Path.Combine(folder, "ch1.cbz"), PngBytes());
        var entry = new LibraryTitleLoader().BuildEntry(folder);
        Assert.NotNull(entry);

        var path = new LibraryCoverCache(_cache).EnsureThumbnail(entry, CancellationToken.None);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.NotNull(LibraryCoverCache.DecodeFile(path));
    }

    [Fact]
    public void MissingSourceResolvesToNull()
    {
        var entry = new LibraryIndexEntry(
            "gone", Path.Combine(_library, "gone"), DateTime.UtcNow, 1,
            "ch1", "ch1", DateTime.UtcNow, "archive:ch1.cbz#1", string.Empty);

        Assert.Null(new LibraryCoverCache(_cache).EnsureThumbnail(entry, CancellationToken.None));
    }

    [Fact]
    public void ThumbnailKeyIsStableAndSized()
    {
        var first = LibraryCoverCache.KeyFor("cover:cover.png#123");
        var second = LibraryCoverCache.KeyFor("cover:cover.png#123");
        var other = LibraryCoverCache.KeyFor("cover:cover.png#124");

        Assert.Equal(16, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void PurgeExceptRemovesOnlyOrphans()
    {
        var keep = Path.Combine(_cache, "keep.jpg");
        var orphan = Path.Combine(_cache, "orphan.jpg");
        File.WriteAllBytes(keep, [1]);
        File.WriteAllBytes(orphan, [2]);

        new LibraryCoverCache(_cache).PurgeExcept([keep]);

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(orphan));
    }
}
