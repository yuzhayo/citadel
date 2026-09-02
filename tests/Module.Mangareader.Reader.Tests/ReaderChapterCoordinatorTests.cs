using Module.Mangareader.ShareLogic;
using System.IO;

namespace Module.Mangareader;

public sealed class ReaderChapterCoordinatorTests
{
    [Fact]
    public void StartLoad_PublishesIntoStableCollectionAndRemovesBlocker()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(2);
            var viewport = new TestViewport();
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            var loader = ImmediateLoader();
            using var coordinator = Create(title, 0, viewport, status, state, loader);
            var published = coordinator.Surfaces;

            coordinator.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Same(published, coordinator.Surfaces);
            Assert.Equal([0, 1], published.Select(surface => surface.ChapterIndex));
            Assert.Equal(ChapterSurfaceRole.Active, published[0].Role);
            Assert.Equal(ChapterSurfaceRole.Next, published[1].Role);
            Assert.False(state.IsLoading);
            Assert.False(state.HasError);
            Assert.False(status.IsVisible);
            Assert.Equal(1, status.HideCount);
            Assert.Equal(800, viewport.ContentWidth);
        });
    }

    [Fact]
    public void StartLoad_UnmeasuredViewportDoesNotIssueOnePixelDecode()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(1);
            var viewport = new TestViewport
            {
                ViewportWidth = 0,
            };
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            var loadCalls = 0;
            var loader = new DelegateChapterLoader((chapter, request, _) =>
            {
                loadCalls++;
                return Task.FromResult(DelegateChapterLoader.Chapter(chapter, request));
            });
            using var coordinator = Create(title, 0, viewport, status, state, loader);

            coordinator.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Equal(0, loadCalls);
            Assert.Empty(coordinator.Surfaces);
            Assert.True(state.HasError);
            Assert.True(status.HasError);
            Assert.Contains("viewport", status.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void BoundaryPreparation_InsertsPreviousAndPreservesViewportAnchor()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(3);
            var viewport = new TestViewport();
            var status = new TestStatusHost();
            var state = new ReaderSessionState();
            using var coordinator = Create(title, 1, viewport, status, state, ImmediateLoader());
            coordinator.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Equal([0, 1, 2], coordinator.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal(1000, viewport.VerticalOffset, 3);
            coordinator.PrepareBoundaryAsync(-1, CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal([0, 1, 2], coordinator.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal(1000, viewport.VerticalOffset, 3);
            Assert.Equal(ReaderActivityOrigin.LayoutRestore, viewport.VerticalScrolls[^1].Origin);
        });
    }

    [Fact]
    public void StartLoad_DoesNotCommitPreviousDuringReentrantInitialLayout()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(3);
            var viewport = new TestViewport
            {
                RaiseLayoutChangedReentrantly = true,
            };
            var state = new ReaderSessionState();
            using var coordinator = Create(
                title,
                1,
                viewport,
                new TestStatusHost(),
                state,
                ImmediateLoader());
            var commits = new List<int>();
            coordinator.ActiveChapterChanged += (_, _) => commits.Add(coordinator.ActiveChapterIndex);

            coordinator.StartLoadAsync().GetAwaiter().GetResult();
            WpfTest.PumpFor(TimeSpan.FromMilliseconds(30));

            Assert.Equal(1, coordinator.ActiveChapterIndex);
            Assert.Equal([0, 1, 2], coordinator.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Empty(commits);
            Assert.False(state.IsLoading);
            Assert.False(state.IsTransitioning);
        });
    }

    [Fact]
    public void ViewportResize_ReconfiguresAfterTheNewLayoutValueIsObservable()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(1);
            var viewport = new TestViewport { ViewportWidth = 800 };
            using var coordinator = Create(
                title,
                0,
                viewport,
                new TestStatusHost(),
                new ReaderSessionState(),
                ImmediateLoader());
            coordinator.StartLoadAsync().GetAwaiter().GetResult();
            Assert.Equal(800, viewport.ContentWidth);

            // WPF raises SizeChanged before ScrollViewer.ViewportWidth has
            // necessarily committed its post-layout value.
            viewport.RaiseSizeChanged();
            viewport.ViewportWidth = 500;
            WpfTest.PumpUntil(() => Math.Abs(viewport.ContentWidth - 500) < 0.001);

            Assert.Equal(500, viewport.ContentWidth);
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
            using var coordinator = Create(title, 0, viewport, status, state, ImmediateLoader());
            var history = new List<int>();
            coordinator.ActiveChapterChanged += (_, _) => history.Add(coordinator.ActiveChapterIndex);
            coordinator.StartLoadAsync().GetAwaiter().GetResult();

            viewport.ScrollToVerticalOffset(1100, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => coordinator.ActiveChapterIndex == 1
                && coordinator.Surfaces.Any(surface => surface.ChapterIndex == 2)
                && !state.IsTransitioning);

            Assert.Equal([0, 1, 2], coordinator.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal([1], history);

            viewport.ScrollToVerticalOffset(100, ReaderActivityOrigin.ManualScroll);
            WpfTest.PumpUntil(() => coordinator.ActiveChapterIndex == 0
                && coordinator.Surfaces.All(surface => surface.ChapterIndex <= 1)
                && !state.IsTransitioning);

            Assert.Equal([0, 1], coordinator.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal([1, 0], history);
        });
    }

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
            using var coordinator = Create(title, 0, viewport, status, state, loader);
            coordinator.StartLoadAsync().GetAwaiter().GetResult();
            viewport.ScrollToVerticalOffset(500, ReaderActivityOrigin.ManualScroll);
            var commits = new List<int>();
            coordinator.ActiveChapterChanged += (_, _) => commits.Add(coordinator.ActiveChapterIndex);

            var stale = coordinator.NavigateToChapterAsync(2);
            Assert.False(stale.IsCompleted);
            var latest = coordinator.NavigateToChapterAsync(1);
            delayedThird.SetResult(DelegateChapterLoader.Chapter(
                title.Chapters[2],
                new ChapterRenderRequest(800, 800, 1, PageRenderQuality.Full)));
            Task.WhenAll(stale, latest).GetAwaiter().GetResult();

            Assert.Equal(1, coordinator.ActiveChapterIndex);
            Assert.Equal([1], commits);
            Assert.Contains(
                viewport.VerticalScrolls,
                scroll => scroll.Offset == 0 && scroll.Origin == ReaderActivityOrigin.ChapterJump);
            Assert.False(state.IsLoading);
            Assert.False(state.IsTransitioning);
        });
    }

    [Fact]
    public void Dispose_CancelsPendingLoadWithoutUseAfterDispose()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(1);
            var cancellationObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var loader = new DelegateChapterLoader((_, _, token) =>
            {
                var completion = new TaskCompletionSource<LoadedChapter>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() =>
                {
                    cancellationObserved.TrySetResult();
                    completion.TrySetCanceled(token);
                });
                return completion.Task;
            });
            var coordinator = Create(
                title,
                0,
                new TestViewport(),
                new TestStatusHost(),
                new ReaderSessionState(),
                loader);

            var load = coordinator.StartLoadAsync();
            coordinator.Dispose();

            Task.WhenAll(load, cancellationObserved.Task).GetAwaiter().GetResult();
            Assert.Empty(coordinator.Surfaces);
            Assert.True(coordinator.AsyncResourcesDisposed);
        });
    }

    [Fact]
    public void Dispose_WithoutInFlightWorkDisposesAsyncResourcesImmediately()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(1);
            var coordinator = Create(
                title,
                0,
                new TestViewport(),
                new TestStatusHost(),
                new ReaderSessionState(),
                ImmediateLoader());

            coordinator.Dispose();

            Assert.True(coordinator.AsyncResourcesDisposed);
            Assert.Equal(0, coordinator.ActiveAsyncOperationCount);
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
            using var coordinator = Create(
                title,
                0,
                new TestViewport(),
                new TestStatusHost(),
                state,
                loader);
            coordinator.StartLoadAsync().GetAwaiter().GetResult();
            var commits = 0;
            coordinator.ActiveChapterChanged += (_, _) => commits++;

            var stale = coordinator.NavigateToChapterAsync(2);
            var current = coordinator.NavigateToChapterAsync(0);
            delayedThird.SetResult(DelegateChapterLoader.Chapter(
                title.Chapters[2],
                new ChapterRenderRequest(800, 800, 1, PageRenderQuality.Full)));
            Task.WhenAll(stale, current).GetAwaiter().GetResult();

            Assert.Equal(0, coordinator.ActiveChapterIndex);
            Assert.Equal(0, commits);
            Assert.False(state.IsLoading);
            Assert.False(state.IsTransitioning);
        });
    }

    private static ReaderChapterCoordinator Create(
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
