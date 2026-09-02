using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Preload/render-configuration coverage focused on <see cref="ChapterPreloader"/>:
/// the render requests are reconfigured only after the new viewport width is
/// observable, matching WPF raising SizeChanged before ViewportWidth commits.
/// </summary>
public sealed class ChapterPreloaderTests
{
    [Fact]
    public void ViewportResize_ReconfiguresAfterTheNewLayoutValueIsObservable()
    {
        WpfTest.Run(() =>
        {
            var title = ReaderTestContext.Title(1);
            var viewport = new TestViewport { ViewportWidth = 800 };
            using var feature = Create(
                title,
                0,
                viewport,
                new TestStatusHost(),
                new ReaderSessionState(),
                ImmediateLoader());
            feature.StartLoadAsync().GetAwaiter().GetResult();
            Assert.Equal(800, viewport.ContentWidth);

            // WPF raises SizeChanged before ScrollViewer.ViewportWidth has
            // necessarily committed its post-layout value.
            viewport.RaiseSizeChanged();
            viewport.ViewportWidth = 500;
            WpfTest.PumpUntil(() => Math.Abs(viewport.ContentWidth - 500) < 0.001);

            Assert.Equal(500, viewport.ContentWidth);
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
