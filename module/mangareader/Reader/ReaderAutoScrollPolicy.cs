namespace Module.Mangareader;

/// <summary>Pure elapsed-time and interruption rules for Auto-scroll.</summary>
public static class ReaderAutoScrollPolicy
{
    public static double DistanceForElapsed(
        double viewportHeight,
        double secondsPerViewport,
        TimeSpan elapsed)
    {
        var height = double.IsFinite(viewportHeight) ? Math.Max(0, viewportHeight) : 0;
        var speed = ReaderValuePolicy.NormalizeAutoScroll(secondsPerViewport);
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        return height / speed * seconds;
    }

    public static bool StopsFor(ReaderActivityOrigin origin) =>
        origin is not ReaderActivityOrigin.AutoScroll
            and not ReaderActivityOrigin.LayoutRestore;
}
