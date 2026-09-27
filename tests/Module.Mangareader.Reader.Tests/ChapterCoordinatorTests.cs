using System.Collections.Specialized;
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

    [Fact]
    public void AppendingNextChapter_DoesNotForceLayoutOrMoveTheAnchor()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(4);
            var viewport = new TestViewport { ScrollableHeight = 12000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, TallLoader(2000));
            feature.StartLoadAsync().GetAwaiter().GetResult();

            // The window is [0,1] with 2000-tall surfaces. 2500 puts the viewport
            // 500px into chapter 1, whose centre is therefore in chapter 1.
            viewport.ScrollToVerticalOffset(2500, ReaderActivityOrigin.ManualScroll);
            var scrollsBefore = viewport.VerticalScrolls.Count;
            var layoutsBefore = viewport.UpdateLayoutCount;
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 2)
                && !state.IsTransitioning);

            Assert.Equal([0, 1, 2], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal(0, viewport.UpdateLayoutCount - layoutsBefore);
            // Chapter 0 is still the previous surface, so the anchored page did not
            // move and no corrective scroll may be emitted.
            Assert.Equal(2500, viewport.VerticalOffset, 3);
            Assert.Equal(scrollsBefore, viewport.VerticalScrolls.Count);
        });
    }

    [Fact]
    public void BoundaryCrossing_RestoresTheAnchoredPageWhenTheStripShiftsUp()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(4);
            var viewport = new TestViewport { ScrollableHeight = 12000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, TallLoader(2000));
            feature.StartLoadAsync().GetAwaiter().GetResult();

            viewport.ScrollToVerticalOffset(2500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1 && !state.IsTransitioning);

            // Chapter 1 now starts at 2000. 4500 is 500px into chapter 2, so the
            // crossing detaches chapter 0 and the strip shifts up by its 2000px.
            viewport.ScrollToVerticalOffset(4500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 2
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 3)
                && !state.IsTransitioning);

            Assert.Equal([1, 2, 3], feature.Surfaces.Select(surface => surface.ChapterIndex));
            // Chapter 2 is now the second surface, so the same 500px anchor is 2500.
            Assert.Equal(2500, viewport.VerticalOffset, 3);
            Assert.Equal(ReaderActivityOrigin.LayoutRestore, viewport.VerticalScrolls[^1].Origin);
        });
    }

    [Fact]
    public void BoundaryCrossing_HoldsTheDetachedChapterBitmapsUntilTheReaderMovesOn()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(5);
            var viewport = new TestViewport { ScrollableHeight = 20000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, TallLoader(2000));

            var detached = new List<ChapterSurfaceModel>();
            ((INotifyCollectionChanged)feature.Surfaces).CollectionChanged += (_, args) =>
            {
                if (args.OldItems is null) return;
                foreach (ChapterSurfaceModel item in args.OldItems) detached.Add(item);
            };

            feature.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(2500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1 && !state.IsTransitioning);
            viewport.ScrollToVerticalOffset(4500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 2
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 3)
                && !state.IsTransitioning);

            var released = Assert.Single(detached);
            Assert.Equal(0, released.ChapterIndex);
            // Dropping a chapter's worth of unmanaged pixel buffers in the frame that
            // runs the forced layout is the collision this deferral exists to avoid.
            Assert.False(released.IsEvicted);
            Assert.Equal(2500, viewport.VerticalOffset, 3);

            viewport.ScrollToVerticalOffset(3100, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => released.IsEvicted);

            Assert.Equal(2, feature.ActiveChapterIndex);
            Assert.All(feature.Surfaces, surface => Assert.False(surface.IsEvicted));
        });
    }

    [Fact]
    public void ChapterJump_ReleasesEverySurfaceFromTheDiscardedStripAfterLayout()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(5);
            var viewport = new TestViewport { ScrollableHeight = 20000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, TallLoader(2000));

            feature.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(2500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1 && !state.IsTransitioning);
            viewport.ScrollToVerticalOffset(4500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 2 && !state.IsTransitioning);

            var discardedStrip = feature.Surfaces.ToArray();
            Assert.Equal([1, 2, 3], discardedStrip.Select(surface => surface.ChapterIndex));
            Assert.All(discardedStrip, surface => Assert.False(surface.IsEvicted));

            feature.NavigateToChapterAsync(4).GetAwaiter().GetResult();
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 4 && !state.IsTransitioning);

            // A jump discards its entire old strip. Its bitmaps are released only
            // after the replacement strip has completed the layout transaction.
            WpfTest.PumpUntil(() => discardedStrip.All(surface => surface.IsEvicted));
            // The target chapter's own top, in strip coordinates: the previous
            // chapter sits above it, so this is 2000 rather than 0.
            Assert.Equal(2000, viewport.VerticalOffset, 3);
            Assert.Equal(4, feature.ActiveChapterIndex);
        });
    }

    [Fact]
    public void BoundaryCrossing_LeavesTheStripUntouchedWhenTheNewNeighbourFailsToLoad()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(4);
            var viewport = new TestViewport { ScrollableHeight = 12000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, LoaderThatFailsFor(3));

            var detached = new List<ChapterSurfaceModel>();
            ((INotifyCollectionChanged)feature.Surfaces).CollectionChanged += (_, args) =>
            {
                if (args.OldItems is null) return;
                foreach (ChapterSurfaceModel item in args.OldItems) detached.Add(item);
            };

            feature.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(1200, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 2)
                && !state.IsTransitioning);

            // Crossing into chapter 2 has to load chapter 3, which fails. The strip
            // must not be touched: the reader keeps the offset they chose and the
            // extra surface is dropped on the next successful crossing instead.
            viewport.ScrollToVerticalOffset(2200, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 2 && !state.IsTransitioning);

            Assert.Equal([0, 1, 2], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal(2200, viewport.VerticalOffset, 3);
            Assert.Contains("Boom", status.Detail);
            Assert.Empty(detached);
            Assert.All(feature.Surfaces, surface => Assert.False(surface.IsEvicted));
        });
    }

    [Fact]
    public void BoundaryCrossing_HoldsTheDetachedChapterOnlyUntilTheLayoutIsOver()
    {
        WpfTest.Run(() =>
        {
            // 300-tall surfaces inside a 600-tall viewport cross a boundary roughly
            // every 300px, so this crosses three of them back to back.
            var title = ReaderTestContext.Title(6);
            var viewport = new TestViewport { ScrollableHeight = 12000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(title, 0, viewport, status, state, TallLoader(300));

            var detached = new List<ChapterSurfaceModel>();
            ((INotifyCollectionChanged)feature.Surfaces).CollectionChanged += (_, args) =>
            {
                if (args.OldItems is null) return;
                foreach (ChapterSurfaceModel item in args.OldItems) detached.Add(item);
            };

            feature.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(300, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1 && !state.IsTransitioning);
            viewport.ScrollToVerticalOffset(600, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 2 && !state.IsTransitioning);

            // The hold is real: the chapter is out of the window but still owns its
            // bitmaps, so the release did not happen inside the layout transaction.
            var held = Assert.Single(detached);
            Assert.Equal(0, held.ChapterIndex);
            Assert.False(held.IsEvicted);

            // It is not left to depend on further scrolling. Once the dispatcher
            // returns to background work the hold ends without any new input.
            WpfTest.PumpUntil(() => held.IsEvicted);
            Assert.Equal(2, feature.ActiveChapterIndex);
            Assert.All(feature.Surfaces, surface => Assert.False(surface.IsEvicted));

            viewport.ScrollToVerticalOffset(850, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 3
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 4)
                && !state.IsTransitioning);

            Assert.Equal(2, detached.Count);
            Assert.Equal([0, 1], detached.Select(surface => surface.ChapterIndex));
            Assert.True(detached[0].IsEvicted);
            WpfTest.PumpUntil(() => detached[1].IsEvicted);
            Assert.All(feature.Surfaces, surface => Assert.False(surface.IsEvicted));
        });
    }

    [Fact]
    public void BoundaryCrossing_AnchorsAfterTheLoadSoScrollThatAdvancedDuringItIsKept()
    {
        WpfTest.Run(() =>
        {
            // 2000-tall surfaces. The second crossing is the interesting one: it
            // detaches chapter 0, so the strip really does shift, and it has to load
            // chapter 3, which is where the offset advances.
            var title = ReaderTestContext.Title(5);
            var viewport = new TestViewport { ScrollableHeight = 20000 };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var feature = Create(
                title,
                0,
                viewport,
                status,
                state,
                LoaderThatAdvancesOffsetDuringLoad(viewport, "3.cbz", 400));
            feature.StartLoadAsync().GetAwaiter().GetResult();

            viewport.ScrollToVerticalOffset(2500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 1
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 2)
                && !state.IsTransitioning);
            Assert.Equal(2500, viewport.VerticalOffset, 3);

            viewport.ScrollToVerticalOffset(4500, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => feature.ActiveChapterIndex == 2
                && feature.Surfaces.Any(surface => surface.ChapterIndex == 3)
                && !state.IsTransitioning);

            Assert.Equal([1, 2, 3], feature.Surfaces.Select(surface => surface.ChapterIndex));
            // Loading chapter 3 advanced the offset by 400, the way the momentum
            // glide does every vsync. That put the reader 900px into chapter 2, and
            // chapter 2 is now the second surface, so 900 + 2000 is where they have to
            // stay. Anchoring before the load would restore 500 + 2000 and drag them
            // 400px backwards, which is the chapter-entry jump.
            Assert.Equal(2900, viewport.VerticalOffset, 3);
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

    private static DelegateChapterLoader TallLoader(double height) =>
        new((chapter, request, _) =>
            Task.FromResult(DelegateChapterLoader.Chapter(chapter, request, height)));

    private static DelegateChapterLoader LoaderThatFailsFor(int chapterIndex) =>
        new((chapter, request, _) => chapter.FilePath == $"{chapterIndex}.cbz"
            ? throw new InvalidOperationException("Boom")
            : Task.FromResult(DelegateChapterLoader.Chapter(chapter, request)));

    /// <summary>
    /// Advances the viewport while <paramref name="advancingChapter"/> loads. That is
    /// the neighbour-load window the momentum glide writes VerticalOffset in on every
    /// vsync, simulated deterministically.
    /// </summary>
    private static DelegateChapterLoader LoaderThatAdvancesOffsetDuringLoad(
        TestViewport viewport,
        string advancingChapter,
        double delta) =>
        new((chapter, request, _) =>
        {
            if (chapter.FilePath == advancingChapter)
            {
                viewport.ScrollToVerticalOffset(
                    viewport.VerticalOffset + delta,
                    ReaderActivityOrigin.ManualWheel);
            }

            return Task.FromResult(DelegateChapterLoader.Chapter(chapter, request, 2000));
        });
}
