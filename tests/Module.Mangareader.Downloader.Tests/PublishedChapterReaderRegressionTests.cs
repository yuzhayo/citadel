using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using Module.Mangareader.Archive;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The Reader regression gate: a chapter published by the Downloader carries a
/// non-image provenance entry, and the existing reader path must keep ignoring
/// it while still opening the archive and selecting the first supported image
/// as the cover.
/// </summary>
public sealed class PublishedChapterReaderRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.ReaderRegression.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _library;

    public PublishedChapterReaderRegressionTests()
    {
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_library);
    }

    [Fact]
    public async Task PublishedCbzOpensAndItsProvenanceEntryStaysInvisibleToTheReader()
    {
        var published = await PublishAsync(3);
        var reader = new ArchivePageReader();

        Assert.True(File.Exists(published));
        Assert.True(reader.IsSupportedArchive(published));

        var pages = reader.ReadPages(published, CancellationToken.None);

        // Exactly the images, in natural order: META-INF/citadel-source.json is
        // present in the archive but must never reach the page list.
        Assert.Equal(3, pages.Count);
        Assert.Equal(["001.png", "002.png", "003.png"], pages.Select(page => page.Name));
        Assert.All(pages, page => Assert.NotEmpty(page.Bytes));

        using var archive = ZipFile.OpenRead(published);
        Assert.NotNull(archive.GetEntry(CbzChapterPublisher.SourceManifestEntryName));
    }

    [Fact]
    public async Task CoverStillSelectsTheFirstSupportedImageOfAPublishedChapter()
    {
        var published = await PublishAsync(2);

        // The cover loader selects ReadPages(...).FirstOrDefault(), so the first
        // entry must be the first page and must decode as an image.
        var cover = new ArchivePageReader()
            .ReadPages(published, CancellationToken.None)
            .FirstOrDefault();

        Assert.NotNull(cover);
        Assert.Equal("001.png", cover!.Name);

        using var stream = new MemoryStream(cover.Bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.NotEmpty(decoder.Frames);
    }

    private async Task<string> PublishAsync(int pageCount)
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);

        var pages = new List<StagedPage>(pageCount);
        for (var ordinal = 0; ordinal < pageCount; ordinal++)
        {
            var path = Path.Combine(staging, $"page-{ordinal}.png");
            await File.WriteAllBytesAsync(path, Png(24 + ordinal));
            pages.Add(new StagedPage(ordinal, path, "png"));
        }

        var target = new DownloadTarget(_library, "Regression Title", "0001 - Chapter 1 [Official].cbz");
        var outcome = await new CbzChapterPublisher().PublishAsync(
            new RemoteChapterIdentity(
                "comix",
                new RemoteTitleIdentity("comix", "12947", "dy88", "the-novels-extra"),
                "chapter-1",
                "1",
                new RemoteGroupIdentity("comix", "9897")),
            "Official",
            "sha256:regression",
            pages,
            target,
            CancellationToken.None);

        Assert.True(outcome.Published, outcome.ConflictReason);
        return outcome.Path!;
    }

    private static byte[] Png(int size)
    {
        var bitmap = new WriteableBitmap(
            size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x30;
            pixels[offset + 1] = 0x60;
            pixels[offset + 2] = 0x90;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
