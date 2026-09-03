using System.Diagnostics;
using System.IO;
using Module.Mangareader.Archive;

namespace Module.Mangareader.Features.Rar;

public sealed record RarPage(string Name, byte[] Bytes);

/// <summary>
/// The single adapter around the RAR payload shipped with MangaReader.
/// Readers and Cover Builder never invoke Rar.exe directly.
/// </summary>
public sealed class RarArchiveFeature
{
    private static readonly HashSet<string> ImageExtensions = new(
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);
    private readonly string _executablePath;

    public RarArchiveFeature(string? executablePath = null)
    {
        _executablePath = executablePath ?? Path.Combine(
            Path.GetDirectoryName(typeof(RarArchiveFeature).Assembly.Location)!,
            "Features",
            "Rar",
            "Rar.exe");
    }

    public IReadOnlyList<RarPage> ReadPages(
        string archivePath,
        CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var extractionRoot = Path.Combine(
            Path.GetTempPath(),
            "Citadel.MangaReader.Rar",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);

        try
        {
            Run(
                ["x", "-inul", "-o+", "-y", "-p-", "--", archivePath,
                    extractionRoot + Path.DirectorySeparatorChar],
                cancellationToken);

            return Directory
                .EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                .Select(path => new RarPage(
                    Path.GetRelativePath(extractionRoot, path).Replace('\\', '/'),
                    ReadAllBytes(path, cancellationToken)))
                .ToArray();
        }
        finally
        {
            TryDeleteDirectory(extractionRoot);
        }
    }

    public void WriteCover(
        string sourcePath,
        string temporaryPath,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var sourceFormat = new ArchiveSignatureDetector().Detect(sourcePath).Format;
        if (sourceFormat is not (ArchiveFormat.Rar4 or ArchiveFormat.Rar5))
            throw new InvalidDataException("The chapter is not a RAR archive.");

        File.Copy(sourcePath, temporaryPath, overwrite: false);
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            "Citadel.MangaReader.Rar",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        var coverPath = Path.Combine(stagingRoot, CoverArchiveWriter.GeneratedCoverEntryName);

        try
        {
            File.WriteAllBytes(coverPath, pngBytes);
            Run(
                ["a", "-inul", "-ep", "-o+", "-y", "--", temporaryPath, coverPath],
                cancellationToken);
            Run(["t", "-inul", "-p-", "--", temporaryPath], cancellationToken);

            var rewrittenFormat = new ArchiveSignatureDetector().Detect(temporaryPath).Format;
            if (rewrittenFormat is not (ArchiveFormat.Rar4 or ArchiveFormat.Rar5))
                throw new InvalidDataException("Rar.exe did not produce a readable RAR archive.");

            var covers = ReadPages(temporaryPath, cancellationToken)
                .Where(page => string.Equals(
                    page.Name,
                    CoverArchiveWriter.GeneratedCoverEntryName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (covers.Length != 1 || !covers[0].Bytes.AsSpan().SequenceEqual(pngBytes))
                throw new InvalidDataException("The rewritten RAR does not contain the requested cover.");
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private void EnsureAvailable()
    {
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException(
                "The MangaReader RAR payload is missing. Reinstall Citadel from the complete release package.",
                _executablePath);
        }
    }

    private void Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Rar.exe could not be started.");
        var stopwatch = Stopwatch.StartNew();
        while (!process.WaitForExit(100))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (stopwatch.Elapsed > CommandTimeout)
            {
                TryKill(process);
                throw new TimeoutException("Rar.exe did not finish within five minutes.");
            }
        }

        if (process.ExitCode != 0)
            throw new InvalidDataException($"Rar.exe failed with exit code {process.ExitCode}.");
    }

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken)
    {
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
