namespace Module.Mangareader;

public static class ReaderViewportStepPolicy
{
    public static double NextTarget(
        double currentOffset,
        double? coalescedTarget,
        double viewportHeight,
        double scrollableHeight,
        int direction)
    {
        if (direction == 0) return Math.Clamp(currentOffset, 0, Math.Max(0, scrollableHeight));
        var current = double.IsFinite(currentOffset) ? currentOffset : 0;
        var basis = coalescedTarget is { } candidate && double.IsFinite(candidate)
            ? candidate
            : current;
        var height = double.IsFinite(viewportHeight) ? Math.Max(0, viewportHeight) : 0;
        var maximum = double.IsFinite(scrollableHeight) ? Math.Max(0, scrollableHeight) : 0;
        return Math.Clamp(basis + (height * 0.9 * Math.Sign(direction)), 0, maximum);
    }
}
