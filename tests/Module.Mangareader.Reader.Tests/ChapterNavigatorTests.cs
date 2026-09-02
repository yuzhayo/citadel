using System.IO;
using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Jump/navigation coverage focused on <see cref="ChapterNavigator"/>: the
/// latest-request-wins generation guard and landing on page one. Driven through
/// the feature because the navigator shares the coordinator/preloader runtime.
/// </summary>
public sealed class ChapterNavigatorTests
{
    [Fact]
    public void RapidChapterSelection_IsLatestRequestWinsAndLandsOnPageOne()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(3);
            var delayedThird = new TaskCompletionSource<LoadedChapter>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdCall = 0;
            var loader = new DelegateChapterLoader((chapter, request, _) =>
            {
                var index = int.Parse(Path.GetFileNameWithoutExtension(chapter.FilePath));
                if (index == 2 && Interlocked.Increment(ref thirdCall) == 1)
                    return delayedThird.Task;
                return Task.FromResult(DelegateChapterLoader.Chapter(chapter, request));
            });
            var viewport = new TestViewport { ScrollableHeight = 3000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, loader);
            feature.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(500, ReaderActivityOrigin.ManualScroll);
            var commits = new List<int>();
            feature.ActiveChapterChanged += (_, _) => commits.Add(feature.ActiveChapterIndex);

            var stale = feature.NavigateToChapterAsync(2);
            Assert.False(stale.IsCompleted);
            var latest = feature.NavigateToChapterAsync(1);
            delayedThird.SetResult(DelegateChapterLoader.Chapter(
                title.Chapters[2],
                new ChapterRenderRequest(800, 800, 1, PageRenderQuality.Full)));
            Task.WhenAll(stale, latest).GetAwaiter().GetResult();

            Assert.Equal(1, feature.ActiveChapterIndex);
            Assert.Equal([1], commits);
            Assert.Contains(
                viewport.VerticalScrolls,
                scroll => scroll.Offset == 0 && scroll.Origin == ReaderActivityOrigin.ChapterJump);
            Assert.False(state.IsLoading);
            Assert.False(state.IsTransitioning);
        });
    }

    [Fact]
    public void SelectingCurrentChapter_CancelsAnOlderPendingJump()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(3);
            var delayedThird = new TaskCompletionSource<LoadedChapter>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var loader = new DelegateChapterLoader((chapter, request, _) =>
            {
                var index = int.Parse(Path.GetFileNameWithoutExtension(chapter.FilePath));
                return index == 2
                    ? delayedThird.Task
                    : Task.FromResult(DelegateChapterLoader.Chapter(chapter, request));
            });
            var state = new ReaderSessionState();
            using var feature = Create(
                title,
                0,
                new TestViewport(),
                new TestStatusHost(),
                state,
                loader);
            feature.StartLoadAsync().GetAwaiter().GetResult();
            var commits = 0;
            feature.ActiveChapterChanged += (_, _) => commits++;

            var stale = feature.NavigateToChapterAsync(2);
            var current = feature.NavigateToChapterAsync(0);
            delayedThird.SetResult(DelegateChapterLoader.Chapter(
                title.Chapters[2],
                new ChapterRenderRequest(800, 800, 1, PageRenderQuality.Full)));
            Task.WhenAll(stale, current).GetAwaiter().GetResult();

            Assert.Equal(0, feature.ActiveChapterIndex);
            Assert.Equal(0, commits);
            Assert.False(state.IsLoading);
            Assert.False(state.IsTransitioning);
        });
    }

    private static ChapterLoadingFeature Create(
        MangaTitle title,
        int initialIndex,
        TestViewport viewport,
        TestStatusHost status,
        ReaderSessionState state,
        IReaderChapterLoader loader) =>
        new(
            title,
            title.Chapters[initialIndex],
            viewport,
            status,
            state,
            new ReaderActivityHub(),
            loader);
}
