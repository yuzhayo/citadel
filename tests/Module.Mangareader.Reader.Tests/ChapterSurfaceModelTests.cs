using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Eviction coverage for <see cref="ChapterSurfaceModel"/>: releasing the decoded
/// bitmaps must not change anything the reader lays out, because ReaderWindow.xaml
/// binds page height explicitly and binds the source by name.
/// </summary>
public sealed class ChapterSurfaceModelTests
{
    [Fact]
    public void Evict_ReleasesEveryBitmapAndLeavesThePageBoxesIntact()
    {
        WpfTest.Run(() =>
        {
            var surface = Surface(PageRenderQuality.Full, out _, out var secondBitmap);

            Assert.True(surface.IsFullQuality);
            Assert.False(surface.IsEvicted);
            Assert.Equal(2, surface.Pages.Count);
            Assert.All(surface.Pages, page => Assert.NotNull(page.Bitmap));

            surface.Evict();

            Assert.True(surface.IsEvicted);
            Assert.False(surface.IsFullQuality);
            Assert.Equal(0, surface.EstimatedBitmapBytes);
            Assert.Equal(800, surface.SurfaceWidth);
            Assert.Equal(2000, surface.SurfaceHeight);
            Assert.Equal(2, surface.Pages.Count);
            Assert.All(surface.Pages, page => Assert.Null(page.Bitmap));
            Assert.Equal(["page-1", "page-2"], surface.Pages.Select(page => page.Name));
            Assert.Equal([800d, 600d], surface.Pages.Select(page => page.DisplayWidth));
            Assert.Equal([1200d, 800d], surface.Pages.Select(page => page.DisplayHeight));

            // The page boxes are what the layout measures; the identity fields must
            // survive so the surface stays diagnosable after its pixels are gone.
            Assert.Equal("Chapter 1", surface.Chapter.Title);
            Assert.NotNull(secondBitmap);
        });
    }

    [Fact]
    public void Evict_IsIdempotentAndDoesNotResurrectPixels()
    {
        WpfTest.Run(() =>
        {
            var surface = Surface(PageRenderQuality.Full, out _, out _);

            surface.Evict();
            var pagesAfterFirst = surface.Pages;
            surface.Evict();

            Assert.True(surface.IsEvicted);
            Assert.Same(pagesAfterFirst, surface.Pages);
            Assert.All(surface.Pages, page => Assert.Null(page.Bitmap));
        });
    }

    [Fact]
    public void RoleChanges_SurviveEviction()
    {
        WpfTest.Run(() =>
        {
            var surface = Surface(PageRenderQuality.Full, out _, out _);

            surface.SetRole(ChapterSurfaceRole.Next);
            surface.Evict();

            Assert.Equal(ChapterSurfaceRole.Next, surface.Role);
            Assert.Equal(20, surface.ZIndex);
        });
    }

    private static ChapterSurfaceModel Surface(
        PageRenderQuality quality,
        out BitmapSource firstBitmap,
        out BitmapSource secondBitmap)
    {
        // Bgra32 is not indexed, so a null palette is the valid unindexed case.
        firstBitmap = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgra32, palette: null);
        secondBitmap = new WriteableBitmap(6, 6, 96, 96, PixelFormats.Bgra32, palette: null);

        var pages = new[]
        {
            new LoadedPage("page-1", firstBitmap, 1600, 2400, 800, 1200, quality),
            new LoadedPage("page-2", secondBitmap, 1200, 1600, 600, 800, quality),
        };

        var chapter = new LoadedChapter(
            new ChapterInfo("Chapter 1", "1.cbz"),
            pages,
            800,
            2000,
            4L * 16 + 4L * 36,
            quality);

        return new ChapterSurfaceModel(0, chapter, ChapterSurfaceRole.Active);
    }
}
