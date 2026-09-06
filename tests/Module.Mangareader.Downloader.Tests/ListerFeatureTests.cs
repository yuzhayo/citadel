using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Lister answers local availability from the filesystem, so the Catalog detail
/// reflects a file added or removed outside a stale Library view without any
/// scan, and a chapter published by another group stays a distinct variant.
/// </summary>
public sealed class ListerFeatureTests : IDisposable
{
    private const string Folder = "Listed Title";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.Lister.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _library;
    private readonly string _staging;
    private readonly ListerFeature _lister;

    public ListerFeatureTests()
    {
        _library = Path.Combine(_root, "library");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_staging);

        _lister = new ListerFeature(new LocalTitleProbe(), () => _library, (_, _) => Folder);
    }

    [Fact]
    public async Task AvailabilityFollowsTheDiskAndDistinguishesSourceVariants()
    {
        var missing = await _lister.ListAsync(Title(), CancellationToken.None);
        Assert.False(missing.TitleExists);
        Assert.Equal(Folder, missing.LocalFolderName);
        Assert.Equal(0, missing.ChapterCount);

        await PublishAsync("0001 - Chapter 1.cbz", Identity("c1", "1", "9897"));
        await PublishAsync("0002 - Chapter 2.cbz", Identity("c2", "2", "9897"));

        // No Library scan ran; the probe read the folder as it is now.
        var present = await _lister.ListAsync(Title(), CancellationToken.None);
        Assert.True(present.TitleExists);
        Assert.Equal(2, present.ChapterCount);

        Assert.True(ListerFeature.IsLocallyAvailable(present, Identity("c1", "1", "9897")));
        Assert.False(ListerFeature.IsLocallyAvailable(present, Identity("c3", "3", "9897")));

        // The same number from another group is a distinct variant, not a match.
        Assert.False(ListerFeature.IsLocallyAvailable(present, Identity("c1", "1", "77")));

        File.Delete(Path.Combine(_library, Folder, "0002 - Chapter 2.cbz"));

        var afterDelete = await _lister.ListAsync(Title(), CancellationToken.None);
        Assert.Equal(1, afterDelete.ChapterCount);
        Assert.False(ListerFeature.IsLocallyAvailable(afterDelete, Identity("c2", "2", "9897")));
    }

    /// <summary>
    /// An archive Citadel did not publish carries no embedded identity, so it is
    /// matched by chapter number. Without that fallback an older library would be
    /// told to re-download everything it already owns.
    /// </summary>
    [Fact]
    public async Task AnOlderArchiveWithoutProvenanceStillCountsByChapterNumber()
    {
        var folderPath = Path.Combine(_library, Folder);
        Directory.CreateDirectory(folderPath);
        WritePlainCbz(Path.Combine(folderPath, "Listed Title - 0007.cbz"));

        var result = await _lister.ListAsync(Title(), CancellationToken.None);

        Assert.True(result.TitleExists);
        var chapter = Assert.Single(result.Chapters);
        Assert.False(chapter.HasEmbeddedSource);

        Assert.True(ListerFeature.IsLocallyAvailable(result, Identity("c7", "7", "9897")));
        Assert.True(ListerFeature.IsLocallyAvailable(result, Identity("anything", "7", "77")));
        Assert.False(ListerFeature.IsLocallyAvailable(result, Identity("c8", "8", "9897")));
    }

    [Fact]
    public async Task WithoutALibraryRootAvailabilityIsUnknownNotAnError()
    {
        var lister = new ListerFeature(new LocalTitleProbe(), () => null, (_, _) => Folder);

        var result = await lister.ListAsync(Title(), CancellationToken.None);

        Assert.False(result.TitleExists);
        Assert.Empty(result.Chapters);
        Assert.Empty(result.Warnings);
    }

    private static RemoteTitleSummary Title() => new(
        new RemoteTitleIdentity("comix", "12947", "dy88", "/title/dy88-listed-title"),
        Folder,
        CoverUrl: null,
        LatestChapterLabel: null);

    private static RemoteChapterIdentity Identity(string chapterId, string number, string groupId) =>
        new(
            "comix",
            new RemoteTitleIdentity("comix", "12947", "dy88", "/title/dy88-listed-title"),
            chapterId,
            number,
            new RemoteGroupIdentity("comix", groupId));

    private async Task PublishAsync(string fileName, RemoteChapterIdentity identity)
    {
        var pages = await StagePagesAsync(2);
        var outcome = await new CbzChapterPublisher().PublishAsync(
            identity,
            "Group " + identity.Group.GroupId,
            "sha256:lister-test",
            pages,
            new DownloadTarget(_library, Folder, fileName),
            CancellationToken.None);

        Assert.True(outcome.Published, outcome.ConflictReason);
    }

    private static void WritePlainCbz(string path)
    {
        using var archive = System.IO.Compression.ZipFile.Open(
            path,
            System.IO.Compression.ZipArchiveMode.Create);
        using var page = archive.CreateEntry("001.png").Open();
        page.Write(Png(12));
    }

    private async Task<IReadOnlyList<StagedPage>> StagePagesAsync(int count)
    {
        var folder = Path.Combine(_staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var pages = new List<StagedPage>(count);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var path = Path.Combine(folder, $"page-{ordinal:D5}.png");
            await File.WriteAllBytesAsync(path, Png(16 + ordinal));
            pages.Add(new StagedPage(ordinal, path, "png"));
        }

        return pages;
    }

    private static byte[] Png(int size)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x20;
            pixels[offset + 1] = 0x60;
            pixels[offset + 2] = 0xA0;
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
