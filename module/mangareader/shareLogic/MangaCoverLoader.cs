using System.IO;
using System.Windows.Media.Imaging;
using Module.Mangareader.Archive;

namespace Module.Mangareader.ShareLogic;

public sealed class MangaCoverLoader
{
    public const string TitleCoverFileName = "cover.png";

    /// <summary>
    /// Decode width for card covers. Every consumer must pass this same
    /// value: the render cache key is the chapter's file identity plus the
    /// width, so a different width decodes and stores a second copy of the
    /// same cover instead of reusing the cached one.
    /// </summary>
    public const int PreviewPixelWidth = 320;

    private readonly ChapterRenderCache _cache = new();
    private readonly ArchivePageReader _archives = new();

    public Task<BitmapSource?> LoadAsync(
        MangaTitle title,
        int maximumPixelWidth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (maximumPixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPixelWidth));

        return Task.Run(
            () => Load(title, maximumPixelWidth, cancellationToken),
            cancellationToken);
    }

    private BitmapSource? Load(
        MangaTitle title,
        int maximumPixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var titleCover = TryLoadTitleCover(title.FolderPath, maximumPixelWidth);
        if (titleCover is not null) return titleCover;

        var chapter = title.Chapters.FirstOrDefault();
        if (chapter is null || !File.Exists(chapter.FilePath)) return null;

        var chapterFolder = _cache.GetChapterFolder(chapter);
        var cachePath = _cache.GetPagePath(chapterFolder, maximumPixelWidth, 0);
        var cached = _cache.TryReadPage(cachePath);
        if (cached is not null) return cached;

        cancellationToken.ThrowIfCancellationRequested();
        var page = _archives.ReadPages(chapter.FilePath, cancellationToken).FirstOrDefault();
        if (page is null) return null;

        using var payload = new MemoryStream(page.Bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = maximumPixelWidth;
        bitmap.StreamSource = payload;
        bitmap.EndInit();
        bitmap.Freeze();

        try
        {
            _cache.WritePage(cachePath, bitmap);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return bitmap;
    }

    private static BitmapSource? TryLoadTitleCover(string titleFolder, int maximumPixelWidth)
    {
        if (string.IsNullOrWhiteSpace(titleFolder)) return null;

        var coverPath = Path.Combine(titleFolder, TitleCoverFileName);
        if (!File.Exists(coverPath)) return null;

        try
        {
            // OnLoad closes the file immediately, so Auto Cover can replace it later
            // without being blocked by a live WPF image stream.
            using var payload = new FileStream(
                coverPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = maximumPixelWidth;
            bitmap.StreamSource = payload;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
    }
}
