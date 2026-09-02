namespace Module.Mangareader;

/// <summary>
/// Pure value rules shared by state owners and persistence. Keeping these
/// rules free of WPF/controllers makes loading, migration, and tests agree.
/// </summary>
public static class ReaderValuePolicy
{
    public const double MinimumZoomScale = 0.5;
    public const double MaximumZoomScale = 3.0;
    public const double DefaultZoomScale = 1.0;
    public const double ZoomStep = 0.1;
    public const double MinimumDimPercent = 0;
    public const double MaximumDimPercent = 80;
    public const double DimStep = 5;
    public const double MinimumAutoScrollSeconds = 1;
    public const double MaximumAutoScrollSeconds = 30;
    public const double DefaultAutoScrollSeconds = 5;

    public static double NormalizeZoom(double value)
    {
        var finite = double.IsFinite(value) ? value : DefaultZoomScale;
        return Math.Round(
            Math.Clamp(finite, MinimumZoomScale, MaximumZoomScale),
            1,
            MidpointRounding.AwayFromZero);
    }

    public static double NormalizeDim(double value)
    {
        var finite = double.IsFinite(value) ? value : MinimumDimPercent;
        var snapped = Math.Round(finite / DimStep, MidpointRounding.AwayFromZero) * DimStep;
        return Math.Clamp(snapped, MinimumDimPercent, MaximumDimPercent);
    }

    public static double NormalizeAutoScroll(double value)
    {
        var finite = double.IsFinite(value) ? value : DefaultAutoScrollSeconds;
        return Math.Round(
            Math.Clamp(finite, MinimumAutoScrollSeconds, MaximumAutoScrollSeconds),
            MidpointRounding.AwayFromZero);
    }
}
