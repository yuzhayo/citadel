using System.IO;
using System.IO.Compression;
using Module.Mangareader.Features.Rar;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Archive;

public sealed record ArchivePage(string Name, byte[] Bytes);

/// <summary>One dispatch point for ZIP and RAR chapter pages.</summary>
public sealed class ArchivePageReader
{
    private static readonly HashSet<string> ImageExtensions = new(
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"],
        StringComparer.OrdinalIgnoreCase);

    private readonly ArchiveSignatureDetector _detector = new();
    private readonly RarArchiveFeature _rar = new();

    public bool IsSupportedArchive(string path)
    {
        try
        {
            var format = _detector.Detect(path).Format;
            return format is ArchiveFormat.Zip or ArchiveFormat.Rar4 or ArchiveFormat.Rar5;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<ArchivePage> ReadPages(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var format = _detector.Detect(archivePath).Format;
        var pages = format switch
        {
            ArchiveFormat.Zip => ReadZipPages(archivePath, cancellationToken),
            ArchiveFormat.Rar4 or ArchiveFormat.Rar5 => _rar
                .ReadPages(archivePath, cancellationToken)
                .Select(page => new ArchivePage(page.Name, page.Bytes))
                .ToArray(),
            _ => throw new NotSupportedException(
                "Only ZIP/CBZ and RAR/CBR chapter archives can be read."),
        };

        return pages
            .OrderBy(page => page.Name, NaturalStringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ArchivePage[] ReadZipPages(
        string archivePath,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries
            .Where(entry => entry.Name.Length > 0
                && ImageExtensions.Contains(Path.GetExtension(entry.Name)))
            .Select(entry => new ArchivePage(entry.FullName, ReadEntry(entry, cancellationToken)))
            .ToArray();
    }

    private static byte[] ReadEntry(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        using var source = entry.Open();
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) return destination.ToArray();
            destination.Write(buffer, 0, read);
        }
    }
}
