namespace Module.Mangareader;

/// <summary>Rolling frame-time summary produced by the Reader fps diagnostic.</summary>
public readonly record struct ReaderFrameSummary(
    double Fps,
    double AverageFrameMs,
    double WorstFrameMs,
    int Frames,
    int Hitches);

/// <summary>
/// Pure frame-time statistics for the Reader diagnostic. Kept free of WPF/timers
/// so the fps/hitch math is testable in isolation, like the other Reader policies.
/// </summary>
public static class ReaderFrameStatsPolicy
{
    /// <summary>A frame at or above this duration counts as a hitch (~ below 40fps).</summary>
    public const double HitchThresholdMs = 25;

    public static ReaderFrameSummary Summarize(IReadOnlyList<double> frameMilliseconds)
    {
        if (frameMilliseconds is null || frameMilliseconds.Count == 0) return default;

        var total = 0d;
        var worst = 0d;
        var frames = 0;
        var hitches = 0;
        foreach (var ms in frameMilliseconds)
        {
            if (!double.IsFinite(ms) || ms <= 0) continue;
            frames++;
            total += ms;
            if (ms > worst) worst = ms;
            if (ms >= HitchThresholdMs) hitches++;
        }

        if (frames == 0) return default;
        var average = total / frames;
        return new ReaderFrameSummary(
            average > 0 ? 1000d / average : 0,
            average,
            worst,
            frames,
            hitches);
    }
}
