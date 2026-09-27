using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>
/// One observed viewport change, carrying where the reader had already scrolled to
/// and where the change put them.
/// </summary>
public readonly record struct ReaderScrollSample(
    ReaderActivityOrigin Origin,
    double OffsetBefore,
    double OffsetAfter)
{
    public double Delta => OffsetAfter - OffsetBefore;
    public bool IsVisible(double thresholdPx) => Math.Abs(Delta) > thresholdPx;
}

/// <summary>
/// One second of scroll-continuity samples.
/// <para>
/// A continuous reader is expected to be monotonic, but only for movement the reader
/// asked for. Manual wheel, touch, auto-scroll and overlay steps are all deliberate,
/// so their size is irrelevant. What a reader perceives as a jump is a displacement
/// nobody requested, which in this Reader is a <see cref="ReaderActivityOrigin.LayoutRestore"/>
/// from the chapter coordinator correcting the viewport after the strip changes shape.
/// </para>
/// </summary>
public readonly record struct ReaderScrollSummary(
    int Samples,
    int Restores,
    int Backtracks,
    int ForwardJumps,
    double WorstBacktrackPx,
    double WorstForwardJumpPx)
{
    public static ReaderScrollSummary Empty => new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Pure scroll-continuity statistics for the Reader diagnostic, kept free of WPF so
/// the math is testable in isolation like the other Reader policies.
/// </summary>
public static class ReaderScrollContinuityPolicy
{
    /// <summary>
    /// Sub-pixel movement is layout rounding, not something a reader perceives. Two
    /// device pixels is the point where a correction becomes visible in motion.
    /// </summary>
    public const double VisibleJumpThresholdPx = 2d;

    /// <summary>
    /// The only origin that counts as a jump. Everything else is a deliberate move:
    /// wheel, touch, keyboard, auto-scroll, overlay step, and chapter jump, where
    /// moving the viewport is exactly what the reader asked for.
    /// </summary>
    public const ReaderActivityOrigin JumpOrigin = ReaderActivityOrigin.LayoutRestore;

    public static ReaderScrollSummary Summarize(IReadOnlyList<ReaderScrollSample> samples)
    {
        if (samples is null || samples.Count == 0) return ReaderScrollSummary.Empty;

        var counted = 0;
        var restores = 0;
        var backtracks = 0;
        var forwardJumps = 0;
        var worstBacktrack = 0d;
        var worstForward = 0d;

        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.OffsetBefore) || !double.IsFinite(sample.OffsetAfter)) continue;
            counted++;
            if (sample.Origin != JumpOrigin) continue;

            restores++;
            var delta = sample.Delta;
            if (!sample.IsVisible(VisibleJumpThresholdPx)) continue;

            if (delta < 0)
            {
                backtracks++;
                worstBacktrack = Math.Min(worstBacktrack, delta);
            }
            else
            {
                forwardJumps++;
                worstForward = Math.Max(worstForward, delta);
            }
        }

        return new ReaderScrollSummary(
            counted,
            restores,
            backtracks,
            forwardJumps,
            worstBacktrack,
            worstForward);
    }
}
