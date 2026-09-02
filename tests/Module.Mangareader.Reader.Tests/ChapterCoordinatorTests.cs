using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Surface-management coverage focused on <see cref="ChapterCoordinator"/>: the
/// ordered insert with viewport-anchor preservation and the rolling-window
/// rotation with one history commit per active-chapter change. Driven through the
/// feature because surfaces are populated by the navigator/preloader.
/// </summary>
public sealed class ChapterCoordinatorTests
{
    [Fact]
    public void BoundaryPreparation_InsertsPreviousAndPreservesViewportAnchor()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(3);
            var viewport = new TestViewport();
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 1, viewport, status, state, ImmediateLoader());
            feature.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Equal([0, 1, 2], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal(1000, viewport.VerticalOffset, 3);
            feature.PrepareBoundaryAsync(-1, CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal([0, 1, 2], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal(1000, viewport.VerticalOffset, 3);
            Assert.Equal(ReaderActivityOrigin.LayoutRestore, viewport.VerticalScrolls[^1].Origin);
        });
    }

    [Fact]
    public void RollingWindow_RotatesForwardAndReverseAndEmitsHistoryOncePerCommit()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(3);
            var viewport = new TestViewport { ScrollableHeight = 3000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, ImmediateLoader());
            var history = new List<int>();
            feature.ActiveChapterChanged += (_, _) => history.Add(feature.ActiveChapterIndex);
            feature.StartLoadAsync().GetAwaiter().GetResult();

            viewport.ScrollToVerticalOffset(1100, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 2)
                && !state.IsTransitioning);

            Assert.Equal([0, 1, 2], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal([1], history);

            viewport.ScrollToVerticalOffset(100, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 0
                && feature.Surfaces.All(surface => surface.ChapterIndex <= 1)
                && !state.IsTransitioning);

            Assert.Equal([0, 1], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal([1, 0], history);
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

    private static DelegateChapterLoader ImmediateLoader() =>
        new((chapter, request, _) =>
            Task.FromResult(DelegateChapterLoader.Chapter(chapter, request)));
}
