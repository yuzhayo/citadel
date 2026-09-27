using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public sealed class ChapterDelayedPreloadTests
{
    [Theory]
    [InlineData(1, 5000d, 2100d)]
    [InlineData(2, 2400d, 2400d)]
    public void SinglePageOrScrollLimitStillAllowsNextChapter(int pageCount, double limit, double offset)
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(4);
            var viewport = new TestViewport { ScrollableHeight = limit };
            var loader = new DelegateChapterLoader((chapter, request, _) =>
            {
                var content = Content(chapter, request);
                if (pageCount == 1)
                    content = content with { Pages = [content.Pages[0] with { DisplayHeight = 2000 }] };
                return Task.FromResult(content);
            });
            using var feature = new ChapterLoadingFeature(title, title.Chapters[0], viewport,
                new TestStatusHost(), new ReaderSessionState(), new ReaderActivityHub(), loader);
            feature.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(offset, ReaderActivityOrigin.ManualWheel);
            WpfTest.PumpUntil(() => feature.Surfaces.Any(surface => surface.ChapterIndex == 2));
        });
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(2d)]
    public void NextDecodeWaitsForPageTwoAndDoesNotLockInput(double zoom)
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(4);
            var viewport = new TestViewport { ScrollableHeight = 20000 };
            var state = new ReaderSessionState();
            state.SetZoomScale(zoom);
            var pending = new TaskCompletionSource<LoadedChapter>();
            var loader = new DelegateChapterLoader((chapter, request, _) =>
                chapter.FilePath == "2.cbz" ? pending.Task : Task.FromResult(Content(chapter, request)));
            using var feature = new ChapterLoadingFeature(title, title.Chapters[0], viewport,
                new TestStatusHost(), state, new ReaderActivityHub(), loader);
            feature.StartLoadAsync().GetAwaiter().GetResult();

            viewport.ScrollToVerticalOffset(2100 * zoom, ReaderActivityOrigin.ManualWheel);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1);
            Assert.DoesNotContain(loader.Calls, call => call.Chapter.FilePath == "2.cbz");
            viewport.ScrollToVerticalOffset(2999 * zoom, ReaderActivityOrigin.ManualWheel);
            WpfTest.PumpFor(TimeSpan.FromMilliseconds(15));
            Assert.DoesNotContain(loader.Calls, call => call.Chapter.FilePath == "2.cbz");

            viewport.ScrollToVerticalOffset(3000 * zoom, ReaderActivityOrigin.ManualWheel);
            WpfTest.PumpUntil(() => loader.Calls.Any(call => call.Chapter.FilePath == "2.cbz"));
            var layoutsBeforePreloadCommit = viewport.UpdateLayoutCount;
            Assert.False(state.IsTransitioning);
            Assert.False(state.IsLoading);
            viewport.ScrollToVerticalOffset(3200 * zoom, ReaderActivityOrigin.ManualWheel);
            var call = loader.Calls.Single(call => call.Chapter.FilePath == "2.cbz");
            Assert.Equal(1, call.Request.MaxDecodeParallelism);
            pending.SetResult(Content(call.Chapter, call.Request));
            WpfTest.PumpUntil(() => feature.Surfaces.Any(surface => surface.ChapterIndex == 2));
            Assert.Equal(3200 * zoom, viewport.VerticalOffset, 3);
            Assert.Equal(layoutsBeforePreloadCommit, viewport.UpdateLayoutCount);
            Assert.Single(loader.Calls, call => call.Chapter.FilePath == "2.cbz");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingPreloadCannotRepopulateAfterJumpOrClose(bool close)
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(4);
            var viewport = new TestViewport { ScrollableHeight = 20000 };
            var pending = new TaskCompletionSource<LoadedChapter>();
            var loader = new DelegateChapterLoader((chapter, request, _) =>
                chapter.FilePath == "2.cbz" ? pending.Task : Task.FromResult(Content(chapter, request)));
            using var feature = new ChapterLoadingFeature(title, title.Chapters[0], viewport,
                new TestStatusHost(), new ReaderSessionState(), new ReaderActivityHub(), loader);
            feature.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(3000, ReaderActivityOrigin.ManualWheel);
            WpfTest.PumpUntil(() => loader.Calls.Any(call => call.Chapter.FilePath == "2.cbz"));
            Task jump = Task.CompletedTask;
            if (close) feature.Dispose();
            else jump = feature.NavigateToChapterAsync(0);
            var call = loader.Calls.Single(call => call.Chapter.FilePath == "2.cbz");
            pending.SetResult(Content(call.Chapter, call.Request));
            WpfTest.PumpUntil(() => jump.IsCompleted);
            jump.GetAwaiter().GetResult();
            WpfTest.PumpFor(TimeSpan.FromMilliseconds(25));
            Assert.DoesNotContain(feature.Surfaces, surface => surface.ChapterIndex == 2);
            if (close) Assert.Empty(feature.Surfaces);
            else Assert.Equal(0, feature.ActiveChapterIndex);
        });
    }

    private static LoadedChapter Content(ChapterInfo chapter, ChapterRenderRequest request) =>
        new(chapter,
            [new LoadedPage("1", null, 800, 1000, 800, 1000, request.Quality),
             new LoadedPage("2", null, 800, 1000, 800, 1000, request.Quality)],
            800, 2000, 0, request.Quality);
}
