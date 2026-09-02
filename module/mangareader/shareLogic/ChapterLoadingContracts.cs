using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public enum PageRenderQuality
{
    Preview,
    Full,
}

public sealed record ChapterRenderRequest(
    int DecodeMaximumPixelWidth,
    int DisplayMaximumPixelWidth,
    double DpiScale,
    PageRenderQuality Quality,
    int FullQualityTailPages = 0)
{
    public void Validate()
    {
        if (DecodeMaximumPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(DecodeMaximumPixelWidth));
        if (DisplayMaximumPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(DisplayMaximumPixelWidth));
        if (DpiScale <= 0 || double.IsNaN(DpiScale) || double.IsInfinity(DpiScale))
            throw new ArgumentOutOfRangeException(nameof(DpiScale));
        if (FullQualityTailPages < 0)
            throw new ArgumentOutOfRangeException(nameof(FullQualityTailPages));
    }

    public int DecodeWidthForPage(int pageIndex, int pageCount) =>
        IsFullQualityTail(pageIndex, pageCount)
            ? DisplayMaximumPixelWidth
            : DecodeMaximumPixelWidth;

    public PageRenderQuality QualityForPage(int pageIndex, int pageCount) =>
        IsFullQualityTail(pageIndex, pageCount)
            ? PageRenderQuality.Full
            : Quality;

    private bool IsFullQualityTail(int pageIndex, int pageCount) =>
        Quality == PageRenderQuality.Preview
        && FullQualityTailPages > 0
        && pageIndex >= Math.Max(0, pageCount - FullQualityTailPages);
}

public sealed record LoadedPage(
    string Name,
    BitmapSource Bitmap,
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
