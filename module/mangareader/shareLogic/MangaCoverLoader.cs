using System.IO;
using System.Windows.Media.Imaging;
using Module.Mangareader.Archive;

namespace Module.Mangareader.ShareLogic;

public sealed class MangaCoverLoader
{
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
}
