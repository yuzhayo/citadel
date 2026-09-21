namespace Module.Mangareader;

/// <summary>Pure conversion from wheel input to a Reader viewport distance.</summary>
public static class ReaderManualScrollPolicy
{
    public const double WheelDeltaPerTick = 120d;

    public static double DistanceForWheel(
        int wheelDelta,
        double viewportHeight,
        double percentPerTick)
    {
        if (wheelDelta == 0 || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
            return 0;

        var percent = ReaderValuePolicy.NormalizeManualScroll(percentPerTick);
        return (wheelDelta / WheelDeltaPerTick) * viewportHeight * (percent / 100d);
    }
}
