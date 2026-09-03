using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media.Imaging;
using Module.Mangareader.Archive;

namespace Module.Mangareader.ShareLogic;

public sealed class CbzChapterLoader
{
    private readonly ChapterRenderCache _cache = new();
    private readonly ArchivePageReader _archives = new();

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
            throw new FileNotFoundException("Chapter archive was not found.", chapter.FilePath);
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

        progress?.Report(new ChapterLoadProgress(0, prepared.Length, "Reading archive"));
        var entries = new Dictionary<string, ArchivePage>(StringComparer.Ordinal);
        foreach (var entry in _archives.ReadPages(chapter.FilePath, cancellationToken))
            entries.TryAdd(entry.Name, entry);
        foreach (var index in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = prepared[index].Metadata.Name;
            if (!entries.TryGetValue(name, out var entry))
            {
                throw new InvalidDataException($"Page '{name}' is missing from the archive.");
            }

            prepared[index] = prepared[index] with { Bytes = entry.Bytes };
        }

        return prepared;
    }

    private PreparedPage[] ReadManifestAndPayloads(
        ChapterInfo chapter,
        string chapterCacheFolder,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ChapterLoadProgress(0, 1, "Indexing archive"));
        var entries = _archives.ReadPages(chapter.FilePath, cancellationToken);
        if (entries.Count == 0)
        {
            throw new InvalidDataException("The archive does not contain a supported image page.");
        }

        progress?.Report(new ChapterLoadProgress(0, entries.Count, "Reading archive"));
        var pages = new PreparedPage[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (width, height) = ReadDimensions(entries[index].Bytes);
            pages[index] = new PreparedPage(
                new CachedPageMetadata(entries[index].Name, width, height),
                index,
                entries[index].Bytes);
        }

        TryWriteManifest(
            chapterCacheFolder,
            pages.Select(page => page.Metadata).ToArray());
        return pages;
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

        var bytes = page.Bytes ?? _archives
            .ReadPages(chapter.FilePath, cancellationToken)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                page.Metadata.Name,
                StringComparison.Ordinal))?.Bytes
            ?? throw new InvalidDataException($"Page '{page.Metadata.Name}' is missing from the archive.");
        var decodePixelWidth = Math.Min(
            page.Metadata.NaturalPixelWidth,
            decodeMaximumPixelWidth);
        var bitmap = Decode(bytes, decodePixelWidth);
        TryWritePage(cachePath, bitmap);
        return bitmap;
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
