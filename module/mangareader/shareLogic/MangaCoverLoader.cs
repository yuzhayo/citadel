using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public sealed class MangaCoverLoader
{
    private static readonly HashSet<string> SupportedExtensions = new(
        new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" },
        StringComparer.OrdinalIgnoreCase);

    private readonly ChapterRenderCache _cache = new();

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
        using var file = new FileStream(
            chapter.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

        var entry = archive.Entries
            .Where(candidate => candidate.Name.Length > 0
                && SupportedExtensions.Contains(Path.GetExtension(candidate.Name)))
            .OrderBy(candidate => candidate.FullName, NaturalStringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (entry is null) return null;

        using var entryStream = entry.Open();
        using var payload = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = entryStream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            payload.Write(buffer, 0, read);
        }

        payload.Position = 0;
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
