using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Module.Mangareader.ShareLogic;

public sealed record CoverBakeResult(
    string ChapterPath,
    string BackupPath,
    string CoverEntryName);

public sealed class CbzCoverWriter
{
    public const string GeneratedCoverEntryName = "!00000-citadel-cover.png";

    private readonly string _backupRoot;

    public CbzCoverWriter()
    {
        _backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "cover-backups");
    }

    public Task<CoverBakeResult> BakeAsync(
        MangaTitle title,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0) throw new ArgumentException("Cover PNG is empty.", nameof(pngBytes));

        return Task.Run(
            () => Bake(title, pngBytes, cancellationToken),
            cancellationToken);
    }

    private CoverBakeResult Bake(
        MangaTitle title,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        var chapter = title.Chapters.FirstOrDefault()
            ?? throw new InvalidOperationException("The title has no chapter to receive a cover.");
        if (!File.Exists(chapter.FilePath))
            throw new FileNotFoundException("The first chapter was not found.", chapter.FilePath);

        cancellationToken.ThrowIfCancellationRequested();
        var backupPath = CreateBackup(chapter.FilePath);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(chapter.FilePath)!,
            $".{Path.GetFileName(chapter.FilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            RewriteArchive(
                chapter.FilePath,
                temporaryPath,
                pngBytes,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, chapter.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new CoverBakeResult(
            chapter.FilePath,
            backupPath,
            GeneratedCoverEntryName);
    }

    private string CreateBackup(string chapterPath)
    {
        Directory.CreateDirectory(_backupRoot);
        var file = new FileInfo(chapterPath);
        var identity = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        var backupPath = Path.Combine(
            _backupRoot,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{hash}.cbz");
        File.Copy(chapterPath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void RewriteArchive(
        string sourcePath,
        string destinationPath,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        using var sourceFile = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var sourceArchive = new ZipArchive(sourceFile, ZipArchiveMode.Read, leaveOpen: false);
        using var destinationFile = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan);
        using var destinationArchive = new ZipArchive(
            destinationFile,
            ZipArchiveMode.Create,
            leaveOpen: false);

        var cover = destinationArchive.CreateEntry(
            GeneratedCoverEntryName,
            CompressionLevel.NoCompression);
        using (var coverStream = cover.Open())
        {
            coverStream.Write(pngBytes, 0, pngBytes.Length);
        }

        var buffer = new byte[128 * 1024];
        foreach (var sourceEntry in sourceArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceEntry.Name.Length == 0
                || string.Equals(
                    sourceEntry.FullName,
                    GeneratedCoverEntryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationEntry = destinationArchive.CreateEntry(
                sourceEntry.FullName,
                CompressionLevel.NoCompression);
            TryCopyTimestamp(sourceEntry, destinationEntry);
            using var input = sourceEntry.Open();
            using var output = destinationEntry.Open();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
        }
    }

    private static void TryCopyTimestamp(
        ZipArchiveEntry source,
        ZipArchiveEntry destination)
    {
        try
        {
            destination.LastWriteTime = source.LastWriteTime;
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }
}
