using System.IO;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public sealed class ReaderViewportNavigatorTests
{
    [Fact]
    public void RepeatedSteps_CoalesceIntoOneMovingNinetyPercentTarget()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var viewport = new TestViewport
            {
                ViewportHeight = 600,
                ScrollableHeight = 2400,
            };
            var chapters = new TestChapterNavigation();
            using var navigator = new ReaderViewportNavigator(
                ReaderTestContext.Create(state, viewport: viewport, chapters: chapters));

            navigator.StepAsync(1).GetAwaiter().GetResult();
            navigator.StepAsync(1).GetAwaiter().GetResult();
            WpfTest.PumpUntil(() => Math.Abs(viewport.VerticalOffset - 1080) < 0.05);

            Assert.Equal(1080, viewport.VerticalOffset, 1);
            Assert.All(
                viewport.VerticalScrolls,
                scroll => Assert.Equal(ReaderActivityOrigin.OverlayStep, scroll.Origin));
        });
    }

    [Fact]
    public void AbsoluteBoundary_PreparesOnceThenDoesNotMoveOrWrap()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var viewport = new TestViewport
            {
                ViewportHeight = 600,
                ScrollableHeight = 2400,
            };
            viewport.ScrollToVerticalOffset(2400, ReaderActivityOrigin.LayoutRestore);
            viewport.VerticalScrolls.Clear();
            var chapters = new TestChapterNavigation { IsAtAbsoluteEnd = true };
            using var navigator = new ReaderViewportNavigator(
                ReaderTestContext.Create(state, viewport: viewport, chapters: chapters));

            navigator.StepAsync(1).GetAwaiter().GetResult();

            Assert.Equal([1], chapters.BoundaryRequests);
            Assert.Empty(viewport.VerticalScrolls);
            Assert.Equal(2400, viewport.VerticalOffset);
        });
    }

    [Fact]
    public void NewBoundaryRequest_CancelsTheOlderPreparation()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var viewport = new TestViewport { ScrollableHeight = 0 };
            var firstCancelled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var chapters = new TestChapterNavigation
            {
                BoundaryHandler = (_, token) =>
                {
                    if (Interlocked.Increment(ref calls) != 1) return Task.CompletedTask;
                    var completion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    token.Register(() =>
                    {
                        firstCancelled.TrySetResult();
                        completion.TrySetCanceled(token);
                    });
                    return completion.Task;
                },
            };
            using var navigator = new ReaderViewportNavigator(
                ReaderTestContext.Create(state, viewport: viewport, chapters: chapters));

            var first = navigator.StepAsync(1);
            var second = navigator.StepAsync(1);
            Task.WhenAll(first, second, firstCancelled.Task).GetAwaiter().GetResult();

            Assert.Equal(2, calls);
            Assert.Equal([1, 1], chapters.BoundaryRequests);
        });
    }

    [Fact]
    public void BoundaryFailure_IsReportedOnceAndDoesNotPoisonTheNextStep()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var viewport = new TestViewport
            {
                ViewportHeight = 600,
                ScrollableHeight = 2400,
            };
            viewport.ScrollToVerticalOffset(2400, ReaderActivityOrigin.LayoutRestore);
            viewport.VerticalScrolls.Clear();

            var calls = 0;
            var chapters = new TestChapterNavigation
            {
                BoundaryHandler = (_, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                        throw new InvalidDataException("broken neighbor");

                    viewport.ScrollableHeight = 3000;
                    return Task.CompletedTask;
                },
            };
            var notifications = new ReaderNotificationHub();
            var warnings = new List<ReaderToastRequestEventArgs>();
            notifications.ToastRequested += (_, warning) => warnings.Add(warning);
            using var navigator = new ReaderViewportNavigator(
                ReaderTestContext.Create(
                    state,
                    viewport: viewport,
                    chapters: chapters,
                    notifications: notifications));

            var failure = Record.Exception(
                () => navigator.StepAsync(1).GetAwaiter().GetResult());

            Assert.Null(failure);
            Assert.Empty(viewport.VerticalScrolls);
            var warning = Assert.Single(warnings);
            Assert.Contains("broken neighbor", warning.Message, StringComparison.Ordinal);

            navigator.StepAsync(1).GetAwaiter().GetResult();
            WpfTest.PumpUntil(() => Math.Abs(viewport.VerticalOffset - 2940) < 0.05);

            Assert.Equal(2, calls);
            Assert.Single(warnings);
            Assert.Equal(2940, viewport.VerticalOffset, 1);
        });
    }
}
