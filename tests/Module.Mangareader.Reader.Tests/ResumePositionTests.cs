using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public sealed class ResumePositionTests
{
    [Fact]
    public void Store_RoundTripsClampsAndClearsPerChapter()
    {
        var path = TempPath();
        try
        {
            var store = new ReadingPositionStore(path);
            Assert.Null(store.Get("a.cbz"));

            store.Set("a.cbz", 0.42);
            store.Set("b.cbz", 5);
            Assert.Equal(0.42, store.Get("a.cbz") ?? double.NaN, 3);
            Assert.Equal(1, store.Get("b.cbz") ?? double.NaN, 3);

            // A fresh instance reads the same persisted file.
            Assert.Equal(0.42, new ReadingPositionStore(path).Get("a.cbz") ?? double.NaN, 3);

            store.Clear("a.cbz");
            Assert.Null(new ReadingPositionStore(path).Get("a.cbz"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Feature_SavesChapterRelativeProgress()
    {
        WpfTest.Run(() =>
        {
            var path = TempPath();
            try
            {
                var store = new ReadingPositionStore(path);
                var title = ReaderTestContext.Title(1);
                var state = new ReaderSessionState();
                state.SetLoading(false);
                var viewport = new TestViewport { ScrollableHeight = 1000 };
                var chapters = new TestChapterNavigation(title.Chapters);
                chapters.AddSurface(Surface(title.Chapters[0], 0, 1000));
                var context = ReaderTestContext.Create(state, viewport: viewport, chapters: chapters);
                using var feature = new ResumePositionFeature(store);
                feature.Attach(context);

                viewport.ScrollToVerticalOffset(500, ReaderActivityOrigin.ManualScroll);
                feature.Dispose();

                Assert.Equal(0.5, store.Get(title.Chapters[0].FilePath) ?? double.NaN, 3);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        });
    }

    [Fact]
    public void Feature_RestoresInitialProgressWhenLoadCompletes()
    {
        WpfTest.Run(() =>
        {
            var path = TempPath();
            try
            {
                var store = new ReadingPositionStore(path);
                var title = ReaderTestContext.Title(1);
                var state = new ReaderSessionState();
                var viewport = new TestViewport { ScrollableHeight = 1000 };
                var chapters = new TestChapterNavigation(title.Chapters);
                chapters.AddSurface(Surface(title.Chapters[0], 0, 1000));
                var content = new ReaderContentContext(
                    title,
                    title.Chapters[0],
                    new DelegateChapterLoader((chapter, request, _) =>
                        Task.FromResult(DelegateChapterLoader.Chapter(chapter, request))),
                    new TestStatusHost(),
                    0.5);
                var context = ReaderTestContext.Create(
                    state, viewport: viewport, chapters: chapters, content: content);
                using var feature = new ResumePositionFeature(store);
                feature.Attach(context);

                state.SetLoading(false);

                Assert.Equal(500, viewport.VerticalOffset, 1);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        });
    }

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"resume-{Guid.NewGuid():N}.json");

    private static ChapterSurfaceModel Surface(ChapterInfo chapter, int index, double height) =>
        new(
            index,
            new LoadedChapter(chapter, [], 800, height, 0, PageRenderQuality.Full),
            ChapterSurfaceRole.Active);
}
