using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.ShareLogic;

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
    public void RealDisposableCbz_LoadsRollsAndJumpsThroughOneCoordinatorPath()
    {
        WpfTest.Run(() =>
        {
            for (var index = 0; index < 3; index++)
                _chapters.Add(CreateChapter(index));
            var title = new MangaTitle("Fixture", _root, _chapters);
            var viewport = new TestViewport { ViewportWidth = 700, ScrollableHeight = 5000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var coordinator = new ReaderChapterCoordinator(
                title,
                _chapters[0],
                viewport,
                status,
                state,
                new ReaderActivityHub(),
                new CbzReaderChapterLoader());
            var commits = new List<int>();
            coordinator.ActiveChapterChanged += (_, _) => commits.Add(coordinator.ActiveChapterIndex);

            coordinator.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Equal([0, 1], coordinator.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.All(coordinator.Surfaces, surface => Assert.Equal(3, surface.Pages.Count));
            Assert.Equal(["page-1.png", "page-2.png", "page-10.png"],
                coordinator.Surfaces[0].Pages.Select(page => page.Name));
            Assert.False(state.IsLoading);
            Assert.False(status.IsVisible);

            viewport.ScrollToVerticalOffset(420, ReaderActivityOrigin.LayoutRestore);
            coordinator.NavigateToChapterAsync(2).GetAwaiter().GetResult();

            Assert.Equal(2, coordinator.ActiveChapterIndex);
            Assert.Equal([2], commits);
            var activeTop = coordinator.Surfaces
                .TakeWhile(surface => surface.ChapterIndex < 2)
                .Sum(surface => surface.SurfaceHeight * state.ZoomScale);
            Assert.Equal(activeTop, viewport.VerticalOffset, 3);
            Assert.Equal(3, Assert.Single(
                coordinator.Surfaces,
                surface => surface.ChapterIndex == 2).Pages.Count);
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
