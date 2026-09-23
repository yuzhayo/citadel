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
    public void Validate()
    {
        if (DecodeMaximumPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(DecodeMaximumPixelWidth));
        if (DisplayMaximumPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(DisplayMaximumPixelWidth));
        if (DpiScale <= 0 || double.IsNaN(DpiScale) || double.IsInfinity(DpiScale))
            throw new ArgumentOutOfRangeException(nameof(DpiScale));
    }

    public int DecodeWidthForPage(int pageIndex, int pageCount) => DecodeMaximumPixelWidth;
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
