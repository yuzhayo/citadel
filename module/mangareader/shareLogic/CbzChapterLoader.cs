using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public sealed class CbzChapterLoader
{
    private static readonly HashSet<string> SupportedExtensions = new(
        new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" },
        StringComparer.OrdinalIgnoreCase);

    private readonly ChapterRenderCache _cache = new();

    public Task<LoadedChapter> LoadAsync(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        return Task.Run(
            () => Load(chapter, request, progress, cancellationToken),
            cancellationToken);
    }

    private LoadedChapter Load(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(chapter.FilePath))
        {
            throw new FileNotFoundException("CBZ chapter was not found.", chapter.FilePath);
        }

        var chapterCacheFolder = _cache.GetChapterFolder(chapter);
        var prepared = PreparePages(
            chapter,
            chapterCacheFolder,
            request,
            progress,
            cancellationToken);

        var pages = new LoadedPage?[prepared.Length];
        var loaded = 0;
        var estimatedBitmapBytes = 0L;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };

        try
        {
            Parallel.For(0, prepared.Length, options, index =>
            {
                var page = prepared[index];
                BitmapSource bitmap;

                try
                {
                    bitmap = LoadBitmap(
                        chapter,
                        page,
                        chapterCacheFolder,
                        request.DecodeWidthForPage(index, prepared.Length),
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new InvalidDataException(
                        $"Could not decode page '{page.Metadata.Name}'.",
                        exception);
                }

                var displayPixelWidth = Math.Min(
                    page.Metadata.NaturalPixelWidth,
                    request.DisplayMaximumPixelWidth);
                var displayScale = (double)displayPixelWidth / page.Metadata.NaturalPixelWidth;
                var displayPixelHeight = page.Metadata.NaturalPixelHeight * displayScale;

                pages[index] = new LoadedPage(
                    page.Metadata.Name,
                    bitmap,
                    page.Metadata.NaturalPixelWidth,
                    page.Metadata.NaturalPixelHeight,
                    displayPixelWidth / request.DpiScale,
                    displayPixelHeight / request.DpiScale,
                    request.QualityForPage(index, prepared.Length));

                Interlocked.Add(
                    ref estimatedBitmapBytes,
                    (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);

                var count = Interlocked.Increment(ref loaded);
                progress?.Report(new ChapterLoadProgress(
                    count,
                    prepared.Length,
                    request.Quality == PageRenderQuality.Full
                        ? "Preparing full chapter"
                        : "Preparing previous preview"));
            });
        }
        catch (AggregateException exception)
        {
            ExceptionDispatchInfo.Capture(exception.GetBaseException()).Throw();
            throw;
        }

        var completedPages = pages.Select(page => page!).ToArray();
        return new LoadedChapter(
            chapter,
            completedPages,
            completedPages.Max(page => page.DisplayWidth),
            completedPages.Sum(page => page.DisplayHeight),
            estimatedBitmapBytes,
            request.Quality);
    }

    private PreparedPage[] PreparePages(
        ChapterInfo chapter,
        string chapterCacheFolder,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var manifest = _cache.TryReadManifest(chapterCacheFolder);
        if (manifest is null)
        {
            return ReadManifestAndPayloads(
                chapter,
                chapterCacheFolder,
                progress,
                cancellationToken);
        }

        var prepared = manifest
            .Select((metadata, index) => new PreparedPage(metadata, index, Bytes: null))
            .ToArray();

        var missing = prepared
            .Where(page => !File.Exists(_cache.GetPagePath(
                chapterCacheFolder,
                request.DecodeWidthForPage(page.Index, prepared.Length),
                page.Index)))
            .Select(page => page.Index)
            .ToArray();

        if (missing.Length == 0)
        {
            progress?.Report(new ChapterLoadProgress(0, prepared.Length, "Reading render cache"));
            return prepared;
        }

        progress?.Report(new ChapterLoadProgress(0, prepared.Length, "Reading CBZ"));
        using var file = OpenFile(chapter.FilePath);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false))
        {
            var entries = BuildEntryMap(archive);
            foreach (var index in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = prepared[index].Metadata.Name;
                if (!entries.TryGetValue(name, out var entry))
                {
                    throw new InvalidDataException($"Page '{name}' is missing from the CBZ.");
                }

                prepared[index] = prepared[index] with
                {
                    Bytes = ReadEntry(entry, cancellationToken),
                };
            }
        }

        return prepared;
    }

    private PreparedPage[] ReadManifestAndPayloads(
        ChapterInfo chapter,
        string chapterCacheFolder,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ChapterLoadProgress(0, 1, "Indexing CBZ"));
        using var file = OpenFile(chapter.FilePath);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false))
        {
            var entries = archive.Entries
                .Where(entry => entry.Name.Length > 0
                    && SupportedExtensions.Contains(Path.GetExtension(entry.Name)))
                .OrderBy(entry => entry.FullName, NaturalStringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (entries.Length == 0)
            {
                throw new InvalidDataException("The CBZ does not contain a supported image page.");
            }

            progress?.Report(new ChapterLoadProgress(0, entries.Length, "Reading CBZ"));
            var pages = new PreparedPage[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = ReadEntry(entries[index], cancellationToken);
                var (width, height) = ReadDimensions(bytes);
                pages[index] = new PreparedPage(
                    new CachedPageMetadata(entries[index].FullName, width, height),
                    index,
                    bytes);
            }

            TryWriteManifest(
                chapterCacheFolder,
                pages.Select(page => page.Metadata).ToArray());
            return pages;
        }
    }

    private BitmapSource LoadBitmap(
        ChapterInfo chapter,
        PreparedPage page,
        string chapterCacheFolder,
        int decodeMaximumPixelWidth,
        CancellationToken cancellationToken)
    {
        var cachePath = _cache.GetPagePath(
            chapterCacheFolder,
            decodeMaximumPixelWidth,
            page.Index);
        var cached = _cache.TryReadPage(cachePath);
        if (cached is not null) return cached;

        var bytes = page.Bytes ?? ReadSingleEntry(
            chapter.FilePath,
            page.Metadata.Name,
            cancellationToken);
        var decodePixelWidth = Math.Min(
            page.Metadata.NaturalPixelWidth,
            decodeMaximumPixelWidth);
        var bitmap = Decode(bytes, decodePixelWidth);
        TryWritePage(cachePath, bitmap);
        return bitmap;
    }

    private static FileStream OpenFile(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);

    private static Dictionary<string, ZipArchiveEntry> BuildEntryMap(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0) continue;
            entries.TryAdd(entry.FullName, entry);
        }
        return entries;
    }

    private static byte[] ReadSingleEntry(
        string archivePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        using var file = OpenFile(archivePath);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false))
        {
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, entryName, StringComparison.Ordinal));
            if (entry is null)
            {
                throw new InvalidDataException($"Page '{entryName}' is missing from the CBZ.");
            }
            return ReadEntry(entry, cancellationToken);
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var source = entry.Open();
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static (int Width, int Height) ReadDimensions(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static BitmapSource Decode(byte[] bytes, int decodePixelWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void TryWriteManifest(
        string chapterCacheFolder,
        IReadOnlyList<CachedPageMetadata> pages)
    {
        try
        {
            _cache.WriteManifest(chapterCacheFolder, pages);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TryWritePage(string path, BitmapSource bitmap)
    {
        try
        {
            _cache.WritePage(path, bitmap);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PreparedPage(
        CachedPageMetadata Metadata,
        int Index,
        byte[]? Bytes);
}
