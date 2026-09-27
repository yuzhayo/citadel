using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public enum PageRenderQuality
{
    Full,
}

public sealed record ChapterRenderRequest(
    int DecodeMaximumPixelWidth,
    int DisplayMaximumPixelWidth,
    double DpiScale,
    PageRenderQuality Quality)
{
    // Null keeps normal foreground loading throughput. A neighbour preload
    // uses one worker so archive decode/cache writes do not saturate the CPU
    // while the reader is scrolling through the current chapter.
    public int? MaxDecodeParallelism { get; init; }

    public void Validate()
    {
        if (DecodeMaximumPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(DecodeMaximumPixelWidth));
        if (DisplayMaximumPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(DisplayMaximumPixelWidth));
        if (DpiScale <= 0 || double.IsNaN(DpiScale) || double.IsInfinity(DpiScale))
            throw new ArgumentOutOfRangeException(nameof(DpiScale));
        if (MaxDecodeParallelism is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDecodeParallelism));
    }

    public int DecodeWidthForPage(int pageIndex, int pageCount) => DecodeMaximumPixelWidth;

    /// <summary>
    /// Display box for a page of the given source size, in DIPs.
    /// <para>
    /// The height is deliberately rounded to a whole device pixel. A fractional
    /// layout height makes neighbouring pages meet mid-pixel, and the reader's Fant
    /// scaler leaves a half-transparent edge row on each of them, so the page
    /// background shows through the join as a seam. Keeping every page a whole
    /// number of device pixels tall also makes the chapter's summed height exact,
    /// which is what the scroll anchor is measured against.
    /// </para>
    /// </summary>
    public (double DisplayWidth, double DisplayHeight) DisplayBoxFor(
        int naturalPixelWidth,
        int naturalPixelHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(naturalPixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(naturalPixelHeight);

        var displayPixelWidth = Math.Min(naturalPixelWidth, DisplayMaximumPixelWidth);
        var displayPixelHeight = (int)Math.Round(
            naturalPixelHeight * ((double)displayPixelWidth / naturalPixelWidth),
            MidpointRounding.AwayFromZero);

        return (displayPixelWidth / DpiScale, displayPixelHeight / DpiScale);
    }
}

/// <summary>
/// One rendered page. <paramref name="Bitmap"/> is null only on a surface that has
/// been evicted: the page box is retained so layout geometry is unchanged, but the
/// decoded pixels were released.
/// </summary>
public sealed record LoadedPage(
    string Name,
    BitmapSource? Bitmap,
    int NaturalPixelWidth,
    int NaturalPixelHeight,
    double DisplayWidth,
    double DisplayHeight,
    PageRenderQuality Quality);

public sealed record LoadedChapter(
    ChapterInfo Chapter,
    IReadOnlyList<LoadedPage> Pages,
    double SurfaceWidth,
    double SurfaceHeight,
    long EstimatedBitmapBytes,
    PageRenderQuality Quality);

public sealed record ChapterLoadProgress(int Loaded, int Total, string Stage);
