using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Module.Mangareader.Archive;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library;

/// <summary>
/// File-backed cover thumbnails for index entries. One fingerprint in, one
/// stable JPEG path out: an unchanged title reuses its file without decoding
/// anything, and a changed title naturally orphans its old file for
/// <see cref="PurgeExcept"/> to collect. Sources mirror the index contract —
/// a `cover.*` file wins by the shared rule, otherwise the first page of the
/// natural-first archive — so the bytes always match the stored fingerprint.
/// Best effort throughout: any failure yields null and the index survives.
/// </summary>
public sealed class LibraryCoverCache : ILibraryCoverThumbnails
{
    private readonly string _cacheDirectory;
    private readonly LibraryTitleLoader _loader;
    private readonly ArchivePageReader _archives = new();

    public LibraryCoverCache(string? cacheDirectory = null, LibraryTitleLoader? loader = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "cover-cache");
        _loader = loader ?? new LibraryTitleLoader();
    }

    public string? EnsureThumbnail(LibraryIndexEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entry.CoverFingerprint)) return null;

        var path = Path.Combine(_cacheDirectory, KeyFor(entry.CoverFingerprint) + ".jpg");
        if (File.Exists(path)) return path;

        try
        {
            var source = ReadCoverSource(entry, cancellationToken);
            if (source is null || source.Length == 0) return null;

            var thumbnail = Decode(source);
            if (thumbnail is null) return null;

            Directory.CreateDirectory(_cacheDirectory);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(thumbnail));
                using (var output = File.Create(temporaryPath))
                {
                    encoder.Save(output);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return path;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or InvalidDataException
            or FileFormatException)
        {
            return null;
        }
    }

    public void PurgeExcept(IReadOnlyCollection<string> keepPaths)
    {
        ArgumentNullException.ThrowIfNull(keepPaths);

        string[] files;
        try
        {
            if (!Directory.Exists(_cacheDirectory)) return;
            files = Directory.GetFiles(_cacheDirectory, "*.jpg", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (keepPaths.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            TryDelete(file);
        }
    }

    /// <summary>
    /// Decodes image bytes to a frozen bitmap at card width. Safe to call on
    /// a pool thread; the frozen result crosses to the UI thread.
    /// </summary>
    public static BitmapSource? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = MangaCoverLoader.PreviewPixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is NotSupportedException
            or FileFormatException
            or InvalidDataException
            or IOException)
        {
            return null;
        }
    }

    public static BitmapSource? DecodeFile(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string KeyFor(string coverFingerprint)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(coverFingerprint));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private byte[]? ReadCoverSource(LibraryIndexEntry entry, CancellationToken cancellationToken)
    {
        var cover = TitleCoverFiles.Enumerate(entry.FolderPath).FirstOrDefault();
        if (cover is not null) return File.ReadAllBytes(cover);

        var chapters = _loader.ListChapters(entry.FolderPath, cancellationToken);
        if (chapters.Count == 0) return null;

        var pages = _archives.ReadPages(chapters[0].FilePath, cancellationToken);
        return pages.FirstOrDefault()?.Bytes;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }
}
