using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.Logic;

public sealed record LoadedPage(string Name, BitmapSource Bitmap);

public sealed record LoadedChapter(
    ChapterInfo Chapter,
    IReadOnlyList<LoadedPage> Pages,
    long EstimatedBitmapBytes);

public sealed record ChapterLoadProgress(int Loaded, int Total, string Stage);

public sealed class CbzChapterLoader
{
    private static readonly HashSet<string> SupportedExtensions = new(
        new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" },
        StringComparer.OrdinalIgnoreCase);

    public Task<LoadedChapter> LoadAsync(
        ChapterInfo chapter,
        int decodePixelWidth,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        if (decodePixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(decodePixelWidth));

        return Task.Run(
            () => Load(chapter, decodePixelWidth, progress, cancellationToken),
            cancellationToken);
    }

    private static LoadedChapter Load(
        ChapterInfo chapter,
        int decodePixelWidth,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(chapter.FilePath))
        {
            throw new FileNotFoundException("CBZ chapter was not found.", chapter.FilePath);
        }

        PagePayload[] payloads;
        using (var file = new FileStream(
                   chapter.FilePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
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

            payloads = new PagePayload[entries.Length];
            progress?.Report(new ChapterLoadProgress(0, entries.Length, "Reading CBZ"));

            for (var index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                payloads[index] = new PagePayload(entries[index].FullName, ReadEntry(entries[index], cancellationToken));
            }
        }

        var pages = new LoadedPage?[payloads.Length];
        var loaded = 0;
        var estimatedBitmapBytes = 0L;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };

        try
        {
            Parallel.For(0, payloads.Length, options, index =>
            {
                var payload = payloads[index];
                var bitmap = Decode(payload.Bytes, decodePixelWidth);
                pages[index] = new LoadedPage(payload.Name, bitmap);
                Interlocked.Add(
                    ref estimatedBitmapBytes,
                    (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);

                var count = Interlocked.Increment(ref loaded);
                progress?.Report(new ChapterLoadProgress(count, payloads.Length, "Decoding pages"));
                payloads[index] = default;
            });
        }
        catch (AggregateException exception)
        {
            throw exception.GetBaseException();
        }

        return new LoadedChapter(
            chapter,
            pages.Select(page => page!).ToArray(),
            estimatedBitmapBytes);
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

    private readonly record struct PagePayload(string Name, byte[] Bytes);
}
