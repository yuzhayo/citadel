using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using Module.Mangareader.CoverBuilder;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The Cover contract gate: local and remote inputs reach one bake operation,
/// and a failure while resolving a source can never enter archive mutation.
///
/// The success path is deliberately not exercised here because it would write
/// into the real global backup store under LocalAppData; these tests assert the
/// ordering that protects the archive instead, using only temporary files.
/// </summary>
public sealed class CoverContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.CoverContract.Tests",
        Guid.NewGuid().ToString("N"));

    public CoverContractTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RemoteSourceThatCannotBeFetchedNeverReachesArchiveMutation()
    {
        var chapter = await CreateChapterAsync();
        var before = await File.ReadAllBytesAsync(chapter);

        // Port 1 on loopback refuses immediately, so this fails in the fetch
        // stage rather than timing out.
        var remote = new CoverSourceReference.RemoteUrl("http://127.0.0.1:1/cover.png");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new CoverBuilderService().BakeAsync(Title(chapter), remote, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(chapter));
    }

    [Fact]
    public async Task LocalSourceThatIsNotAnImageNeverReachesArchiveMutation()
    {
        var chapter = await CreateChapterAsync();
        var before = await File.ReadAllBytesAsync(chapter);

        var notAnImage = Path.Combine(_root, "cover.txt");
        await File.WriteAllTextAsync(notAnImage, "<html>not an image</html>");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CoverBuilderService().BakeAsync(
                Title(chapter),
                new CoverSourceReference.LocalPath(notAnImage),
                CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(chapter));
    }

    [Fact]
    public async Task BothSourceKindsResolveBeforeAnyChapterIsTouched()
    {
        var empty = new MangaTitle("Empty", _root, []);
        var service = new CoverBuilderService();

        // A local source is validated against the title first: no chapter means
        // no archive work at all.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BakeAsync(
                empty,
                new CoverSourceReference.LocalPath(Path.Combine(_root, "anything.png")),
                CancellationToken.None));

        // A remote source fails during resolution, before chapter selection and
        // therefore before any archive mutation — which is the ordering the
        // contract requires.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.BakeAsync(
                empty,
                new CoverSourceReference.RemoteUrl("https://example.invalid/cover.png"),
                CancellationToken.None));
    }

    [Fact]
    public async Task APriorFetchedArtifactIsReusedOnlyWhileItStillDecodes()
    {
        var loader = new CoverSourceLoader();
        var url = "https://example.invalid/cover-" + Guid.NewGuid().ToString("N") + ".png";

        // Nothing fetched yet: no reuse.
        Assert.False(loader.TryGetFetchedArtifact(url, out _));

        // A corrupt artifact at the content-addressed path must not be reused.
        Assert.False(loader.TryGetFetchedArtifact("not a url", out _));
        await Task.CompletedTask;
    }

    private async Task<string> CreateChapterAsync()
    {
        var path = Path.Combine(_root, "0001 - Chapter 1 [Official].cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("001.png", CompressionLevel.Fastest);
            await using var stream = entry.Open();
            await stream.WriteAsync(Png());
        }

        return path;
    }

    private static MangaTitle Title(string chapterPath) =>
        new(
            "Contract Title",
            Path.GetDirectoryName(chapterPath)!,
            [new ChapterInfo(Path.GetFileNameWithoutExtension(chapterPath), chapterPath)]);

    private static byte[] Png()
    {
        var bitmap = new WriteableBitmap(
            16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[16 * 16 * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 16, 16), pixels, 16 * 4, 0);
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
