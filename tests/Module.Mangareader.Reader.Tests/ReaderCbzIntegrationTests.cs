using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public sealed class ReaderCbzIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Reader.Cbz.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<ChapterInfo> _chapters = [];

    public ReaderCbzIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RealDisposableCbz_LoadsRollsAndJumpsThroughOneChapterLoadingPath()
    {
        WpfTest.Run(() =>
        {
            for (var index = 0; index < 3; index++)
                _chapters.Add(CreateChapter(index));
            var title = new MangaTitle("Fixture", _root, _chapters);
            var viewport = new TestViewport { ViewportWidth = 700, ScrollableHeight = 5000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = new ChapterLoadingFeature(
                title,
                _chapters[0],
                viewport,
                status,
                state,
                new ReaderActivityHub(),
                new CbzReaderChapterLoader());
            var commits = new List<int>();
            feature.ActiveChapterChanged += (_, _) => commits.Add(feature.ActiveChapterIndex);

            feature.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Equal([0, 1], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.All(feature.Surfaces, surface => Assert.Equal(3, surface.Pages.Count));
            Assert.Equal(["page-1.png", "page-2.png", "page-10.png"],
                feature.Surfaces[0].Pages.Select(page => page.Name));
            Assert.False(state.IsLoading);
            Assert.False(status.IsVisible);

            viewport.ScrollToVerticalOffset(420, ReaderActivityOrigin.LayoutRestore);
            feature.NavigateToChapterAsync(2).GetAwaiter().GetResult();

            Assert.Equal(2, feature.ActiveChapterIndex);
            Assert.Equal([2], commits);
            var activeTop = feature.Surfaces
                .TakeWhile(surface => surface.ChapterIndex < 2)
                .Sum(surface => surface.SurfaceHeight * state.ZoomScale);
            Assert.Equal(activeTop, viewport.VerticalOffset, 3);
            Assert.Equal(3, Assert.Single(
                feature.Surfaces,
                surface => surface.ChapterIndex == 2).Pages.Count);
        });
    }

    [Fact]
    public void RealDisposableCbr_LoadsThroughTheExistingReaderAdapter()
    {
        WpfTest.Run(() =>
        {
            var chapter = CreateRarChapter();
            _chapters.Add(chapter);

            var loaded = new CbzReaderChapterLoader().LoadAsync(
                chapter,
                new ChapterRenderRequest(700, 700, 1, PageRenderQuality.Full),
                progress: null,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(["page-1.png", "page-2.png"], loaded.Pages.Select(page => page.Name));
        });
    }

    private ChapterInfo CreateChapter(int index)
    {
        var path = Path.Combine(_root, $"chapter-{index + 1}.cbz");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WritePage(archive, "page-10.png", 70 + index, 120);
        WritePage(archive, "page-2.png", 80 + index, 130);
        WritePage(archive, "page-1.png", 90 + index, 140);
        return new ChapterInfo($"Chapter {index + 1}", path);
    }

    private static void WritePage(ZipArchive archive, string name, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x60;
            pixels[offset + 1] = 0x90;
            pixels[offset + 2] = 0xD0;
            pixels[offset + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        encoded.Position = 0;
        using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        encoded.CopyTo(stream);
    }

    private ChapterInfo CreateRarChapter()
    {
        var staging = Path.Combine(_root, "rar-pages");
        Directory.CreateDirectory(staging);
        WritePageFile(Path.Combine(staging, "page-2.png"), 80, 130);
        WritePageFile(Path.Combine(staging, "page-1.png"), 90, 140);
        var chapterPath = Path.Combine(_root, "chapter.cbr");
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "Features", "Rar", "Rar.exe"),
            WorkingDirectory = staging,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "a", "-inul", "-ep", "-o+", "-y", "--", chapterPath, "page-2.png", "page-1.png",
        }) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return new ChapterInfo("Chapter RAR", chapterPath);
    }

    private static void WritePageFile(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset + 3] = 0xFF;
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    public void Dispose()
    {
        var cache = new ChapterRenderCache();
        foreach (var chapter in _chapters)
        {
            try
            {
                var folder = cache.GetChapterFolder(chapter);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (FileNotFoundException)
            {
            }
        }

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
