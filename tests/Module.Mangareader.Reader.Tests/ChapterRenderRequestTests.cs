using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Display-box coverage for <see cref="ChapterRenderRequest"/>. The reader stacks
/// pages in one non-virtualized panel with the page background behind them, so every
/// page height has to be a whole number of device pixels or the join between two
/// pages shows the background through the scaler's soft edge rows.
/// </summary>
public sealed class ChapterRenderRequestTests
{
    private static readonly (int Width, int Height)[] RealPageSizes =
    [
        (800, 1200),
        (1654, 2339),
        (1654, 1178),
        (1280, 1810),
        (1920, 1080),
        (1080, 1920),
        (2475, 3508),
        (720, 1280),
    ];

    public static TheoryData<int, int, double> PageSizes =>
        new()
        {
            { 1654, 2339, 1.0 },
            { 1654, 2339, 1.25 },
            { 1654, 2339, 1.5 },
            { 800, 1200, 1.0 },
            { 1920, 1080, 1.0 },
            { 2475, 3508, 2.0 },
            { 720, 1280, 1.0 },
        };

    [Theory]
    [MemberData(nameof(PageSizes))]
    public void DisplayBox_HeightIsAlwaysAWholeNumberOfDevicePixels(
        int naturalPixelWidth,
        int naturalPixelHeight,
        double dpiScale)
    {
        var request = Request(dpiScale);

        var (_, displayHeight) = request.DisplayBoxFor(naturalPixelWidth, naturalPixelHeight);

        var deviceHeight = displayHeight * dpiScale;
        Assert.Equal(Math.Round(deviceHeight), deviceHeight, 6);
    }

    [Fact]
    public void DisplayBox_StaysWithinHalfADevicePixelOfTheExactAspectHeight()
    {
        var request = Request(1.0);
        const int cap = 1210;

        foreach (var (width, height) in RealPageSizes)
        {
            var (_, displayHeight) = request.DisplayBoxFor(width, height);

            var exactDeviceHeight = height * ((double)Math.Min(width, cap) / width);
            Assert.InRange(
                displayHeight,
                exactDeviceHeight - 0.5,
                exactDeviceHeight + 0.5);
        }
    }

    [Fact]
    public void DisplayBox_PreservesAspectRatioWithinRoundingTolerance()
    {
        var request = Request(1.0);

        foreach (var (width, height) in RealPageSizes)
        {
            var (displayWidth, displayHeight) = request.DisplayBoxFor(width, height);

            // The declared size and the actual aspect ratio can differ by at most the
            // half pixel of rounding, expressed relatively.
            var exactRatio = (double)height / width;
            var actualRatio = displayHeight / displayWidth;
            Assert.InRange(actualRatio, exactRatio - 0.002, exactRatio + 0.002);
        }
    }

    [Fact]
    public void DisplayBox_DoesNotUpscalePagesNarrowerThanTheCap()
    {
        var request = Request(1.0);

        var (displayWidth, _) = request.DisplayBoxFor(640, 960);

        Assert.Equal(640, displayWidth, 6);
    }

    [Fact]
    public void DisplayBox_SumsToAWholeDevicePixelChapterHeight()
    {
        var request = Request(1.0);
        var total = 0d;

        foreach (var (width, height) in RealPageSizes)
        {
            var (_, displayHeight) = request.DisplayBoxFor(width, height);
            total += displayHeight;
        }

        Assert.Equal(Math.Round(total), total, 6);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void DisplayBox_RejectsNonPositiveSourceSizes(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Request(1.0).DisplayBoxFor(width, height));

    private static ChapterRenderRequest Request(double dpiScale) =>
        new(1210, 1210, dpiScale, PageRenderQuality.Full);
}
