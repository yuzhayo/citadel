using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public sealed record CachedPageMetadata(
    string Name,
    int NaturalPixelWidth,
    int NaturalPixelHeight);

/// <summary>
/// Persistent, module-owned render cache. A source fingerprint creates a new
/// folder when the CBZ changes; width variants keep preview and screen-sized
/// renders independent.
/// </summary>
public sealed class ChapterRenderCache
{
    private const string ManifestFileName = "pages.json";
    private readonly string _root;

    public ChapterRenderCache()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "cache");
    }

    public string GetChapterFolder(ChapterInfo chapter)
    {
        var file = new FileInfo(chapter.FilePath);
        var identity = string.Join(
            "|",
            file.FullName.ToUpperInvariant(),
            file.Length,
            file.LastWriteTimeUtc.Ticks);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_root, hash[..24]);
    }

    public string GetPagePath(string chapterFolder, int decodeMaximumPixelWidth, int pageIndex) =>
        Path.Combine(
            chapterFolder,
            $"w{decodeMaximumPixelWidth}",
            $"{pageIndex:D5}.png");

    public IReadOnlyList<CachedPageMetadata>? TryReadManifest(string chapterFolder)
    {
        var path = Path.Combine(chapterFolder, ManifestFileName);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var pages = JsonSerializer.Deserialize<CachedPageMetadata[]>(json);
            return pages is { Length: > 0 }
                && pages.All(IsValid)
                ? pages
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteManifest(
        string chapterFolder,
        IReadOnlyList<CachedPageMetadata> pages)
    {
        Directory.CreateDirectory(chapterFolder);
        var path = Path.Combine(chapterFolder, ManifestFileName);
        WriteAtomically(
            path,
            stream => JsonSerializer.Serialize(stream, pages));
    }

    public BitmapSource? TryReadPage(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            return Decode(stream);
        }
        catch (IOException)
        {
            DeleteInvalid(path);
            return null;
        }
        catch (NotSupportedException)
        {
            DeleteInvalid(path);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WritePage(string path, BitmapSource bitmap)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, stream =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        });
    }

    private static BitmapSource Decode(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void WriteAtomically(string path, Action<Stream> write)
    {
        if (File.Exists(path)) return;

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                write(stream);
            }

            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another load completed the same immutable cache entry first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsValid(CachedPageMetadata page) =>
        !string.IsNullOrWhiteSpace(page.Name)
        && page.NaturalPixelWidth > 0
        && page.NaturalPixelHeight > 0;

    private static void DeleteInvalid(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
