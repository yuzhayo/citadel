using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;
using System.Windows;
using System.Windows.Controls;

namespace Module.Mangareader;

/// <summary>
/// Integration coverage for the ChapterLoading feature as a whole. These exercise
/// the feature end to end (initial-load commit and blocker removal, the
/// unmeasured-viewport guard, the reentrant initial-layout guard, and the
/// tracked-async disposal protocol) because the split pieces share one async
/// runtime owned by the feature.
/// </summary>
public sealed class ChapterLoadingFeatureTests
{
    [Fact]
    public void DefaultCatalog_AttachAndHostStart_RegistersAndLoadsThroughContracts()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(2);
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            var activity = new ReaderActivityHub();
            var viewport = new TestViewport();
            var status = new TestStatusHost();
            var loader = ImmediateLoader();
            var navigation = new ReaderChapterNavigationHub(title, title.Chapters[0]);
            var context = new ReaderFeatureContext(
                state,
                commands,
                viewport,
                navigation,
                navigation,
                new ReaderContentContext(title, title.Chapters[0], loader, status),
                new TestInputEvents(),
                activity,
                new ReaderNotificationHub(),
                CancellationToken.None);
            var hosts = Enum.GetValues<ReaderLayer>()
                .ToDictionary(layer => layer, _ => new ContentControl());
            var catalog = ReaderDefaultFeatureCatalog.Create(
                new Window(),
                state,
                commands,
                activity);
            using var host = new ReaderFeatureHost(context, hosts, catalog);

            host.StartAsync().GetAwaiter().GetResult();
            var callsAfterFirstStart = loader.Calls.Count;
            host.StartAsync().GetAwaiter().GetResult();

            Assert.Equal([0, 1], navigation.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Equal(2, callsAfterFirstStart);
            Assert.Equal(callsAfterFirstStart, loader.Calls.Count);
            Assert.False(state.IsLoading);
            Assert.False(status.IsVisible);
        });
    }

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
            using var feature = Create(title, 0, viewport, status, state, loader);
            var published = feature.Surfaces;

            feature.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Same(published, feature.Surfaces);
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
            using var feature = Create(title, 0, viewport, status, state, loader);

            feature.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Equal(0, loadCalls);
            Assert.Empty(feature.Surfaces);
            Assert.True(state.HasError);
            Assert.True(status.HasError);
            Assert.Contains("viewport", status.Detail, StringComparison.OrdinalIgnoreCase);
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
            using var feature = Create(
                title,
                1,
                viewport,
                new TestStatusHost(),
                state,
                ImmediateLoader());
            var commits = new List<int>();
            feature.ActiveChapterChanged += (_, _) => commits.Add(feature.ActiveChapterIndex);

            feature.StartLoadAsync().GetAwaiter().GetResult();
            WpfTest.PumpFor(TimeSpan.FromMilliseconds(30));

            Assert.Equal(1, feature.ActiveChapterIndex);
            Assert.Equal([0, 1, 2], feature.Surfaces.Select(surface => surface.ChapterIndex));
            Assert.Empty(commits);
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
            var feature = Create(
                title,
                0,
                new TestViewport(),
                new TestStatusHost(),
                new ReaderSessionState(),
                loader);

            var load = feature.StartLoadAsync();
            feature.Dispose();

            Task.WhenAll(load, cancellationObserved.Task).GetAwaiter().GetResult();
            Assert.Empty(feature.Surfaces);
            Assert.True(feature.AsyncResourcesDisposed);
        });
    }

    [Fact]
    public void Dispose_WithoutInFlightWorkDisposesAsyncResourcesImmediately()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(1);
            var feature = Create(
                title,
                0,
                new TestViewport(),
                new TestStatusHost(),
                new ReaderSessionState(),
                ImmediateLoader());

            feature.Dispose();

            Assert.True(feature.AsyncResourcesDisposed);
            Assert.Equal(0, feature.ActiveAsyncOperationCount);
        });
    }

    [Fact]
    public void NavigationHub_SeededThenForwardsToRegisteredFeature()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(2);
            var hub = new ReaderChapterNavigationHub(title, title.Chapters[0]);

            // Seeded metadata is available before any implementation registers,
            // which keeps consumers independent of catalog attach order.
            Assert.Equal(0, hub.ActiveChapterIndex);
            Assert.Equal(title.Title, hub.MangaTitle);
            Assert.False(hub.CanNavigatePrevious);
            Assert.True(hub.CanNavigateNext);

            var state = new ReaderSessionState();
            using var feature = new ChapterLoadingFeature(
                title,
                title.Chapters[0],
                new TestViewport(),
                new TestStatusHost(),
                state,
                new ReaderActivityHub(),
                ImmediateLoader(),
                hub);

            feature.StartLoadAsync().GetAwaiter().GetResult();

            Assert.Same(feature.Surfaces, hub.Surfaces);
            Assert.Equal(feature.ActiveChapterIndex, hub.ActiveChapterIndex);

            hub.NavigateToChapterAsync(1).GetAwaiter().GetResult();

            Assert.Equal(1, hub.ActiveChapterIndex);
            Assert.Equal(1, feature.ActiveChapterIndex);
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
