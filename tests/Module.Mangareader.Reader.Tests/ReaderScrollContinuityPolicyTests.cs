using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>
/// Scroll-continuity coverage for <see cref="ReaderScrollContinuityPolicy"/>. The
/// point of the diagnostic is to tell a frame-time stall apart from a viewport that
/// moved somewhere the reader did not ask for, so the tests pin which of the two a
/// given sample sequence represents.
/// </summary>
public sealed class ReaderScrollContinuityPolicyTests
{
    [Fact]
    public void ASingleBacktrackIsReportedWithItsMagnitude()
    {
        var summary = ReaderScrollContinuityPolicy.Summarize(
        [
            Sample(ReaderActivityOrigin.ManualWheel, 1000, 1100),
            Sample(ReaderActivityOrigin.LayoutRestore, 1100, 700),
        ]);

        Assert.Equal(2, summary.Samples);
        Assert.Equal(1, summary.Backtracks);
        Assert.Equal(0, summary.ForwardJumps);
        Assert.Equal(-400, summary.WorstBacktrackPx, 3);
    }

    [Fact]
    public void OrdinaryForwardScrollingIsNotAJump()
    {
        var summary = ReaderScrollContinuityPolicy.Summarize(
        [
            Sample(ReaderActivityOrigin.ManualWheel, 0, 120),
            Sample(ReaderActivityOrigin.ManualWheel, 120, 260),
            Sample(ReaderActivityOrigin.ManualWheel, 260, 380),
            Sample(ReaderActivityOrigin.ManualTouch, 380, 900),
        ]);

        Assert.Equal(4, summary.Samples);
        Assert.Equal(0, summary.Restores);
        Assert.Equal(0, summary.Backtracks);
        Assert.Equal(0, summary.ForwardJumps);
    }

    [Fact]
    public void ADeliberateChapterJumpIsNotCountedAsAJump()
    {
        var summary = ReaderScrollContinuityPolicy.Summarize(
        [
            Sample(ReaderActivityOrigin.ChapterJump, 12000, 0),
            Sample(ReaderActivityOrigin.OverlayStep, 0, 540),
        ]);

        Assert.Equal(2, summary.Samples);
        Assert.Equal(0, summary.Restores);
        Assert.Equal(0, summary.Backtracks);
        Assert.Equal(0, summary.ForwardJumps);
    }

    [Fact]
    public void SubPixelMovementIsTreatedAsLayoutRounding()
    {
        var summary = ReaderScrollContinuityPolicy.Summarize(
        [
            Sample(ReaderActivityOrigin.ManualWheel, 100, 101.4),
            Sample(ReaderActivityOrigin.ManualWheel, 101.4, 100.3),
        ]);

        Assert.Equal(2, summary.Samples);
        Assert.Equal(0, summary.Backtracks);
        Assert.Equal(0, summary.ForwardJumps);
    }

    [Fact]
    public void ForwardAndBackwardJumpsAreCountedSeparately()
    {
        var summary = ReaderScrollContinuityPolicy.Summarize(
        [
            Sample(ReaderActivityOrigin.LayoutRestore, 0, 300),
            Sample(ReaderActivityOrigin.LayoutRestore, 300, 40),
        ]);

        Assert.Equal(1, summary.ForwardJumps);
        Assert.Equal(1, summary.Backtracks);
        Assert.Equal(300, summary.WorstForwardJumpPx, 3);
        Assert.Equal(-260, summary.WorstBacktrackPx, 3);
    }

    [Fact]
    public void TheWorstCaseIsKeptNotTheLast()
    {
        var summary = ReaderScrollContinuityPolicy.Summarize(
        [
            Sample(ReaderActivityOrigin.LayoutRestore, 5000, 4900),
            Sample(ReaderActivityOrigin.LayoutRestore, 4900, 4890),
            Sample(ReaderActivityOrigin.LayoutRestore, 4890, 4800),
        ]);

        Assert.Equal(3, summary.Restores);
        Assert.Equal(3, summary.Backtracks);
        // The largest single correction, not the cumulative drop: what the eye sees
        // as one jump is one displacement, and -100 is the worst of these three.
        Assert.Equal(-100, summary.WorstBacktrackPx, 3);
    }

    [Fact]
    public void NonFiniteOffsetsAreIgnoredRatherThanReported()
    {
        var summary = ReaderScrollContinuityPolicy.Summarize(
        [
            Sample(ReaderActivityOrigin.ManualWheel, double.NaN, 100),
            Sample(ReaderActivityOrigin.ManualWheel, 100, double.PositiveInfinity),
            Sample(ReaderActivityOrigin.ManualWheel, 100, 180),
        ]);

        Assert.Equal(1, summary.Samples);
        Assert.Equal(0, summary.Backtracks);
    }

    [Fact]
    public void AnEmptyWindowReportsNothingRatherThanThrowing()
    {
        Assert.Equal(ReaderScrollSummary.Empty, ReaderScrollContinuityPolicy.Summarize([]));
        Assert.Equal(ReaderScrollSummary.Empty, ReaderScrollContinuityPolicy.Summarize(null!));
    }

    private static ReaderScrollSample Sample(
        ReaderActivityOrigin origin,
        double before,
        double after) =>
        new(origin, before, after);
}
