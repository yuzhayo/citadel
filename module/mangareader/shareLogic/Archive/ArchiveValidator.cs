using System.IO;
using System.IO.Compression;

namespace Module.Mangareader.Archive;

public enum ArchiveState
{
    Healthy,
    CorruptOrTruncated,
    Unsupported,
    Unknown,
    Missing,
}

public sealed record ArchiveValidation(
    string Path,
    ArchiveFormat Format,
    ArchiveState State,
    ArchiveCapabilities Capabilities,
    string Detail);

/// <summary>
/// Combines signature detection with container-level validation. Detection
/// alone is never trusted: a ZIP that opens but whose central directory is
/// unreadable is reported as corrupt, and recognition never implies that the
/// container may be rewritten.
/// </summary>
public sealed class ArchiveValidator
{
    private readonly ArchiveSignatureDetector _detector;

    public ArchiveValidator(ArchiveSignatureDetector? detector = null) =>
        _detector = detector ?? new ArchiveSignatureDetector();

    public ArchiveValidation Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            var missing = ArchiveCapabilities.For(ArchiveFormat.Unknown);
            return new ArchiveValidation(
                path,
                ArchiveFormat.Unknown,
                ArchiveState.Missing,
                missing,
                "The chapter file was not found.");
        }

        var detection = _detector.Detect(path);
        var capabilities = ArchiveCapabilities.For(detection.Format);

        if (!detection.Recognized)
        {
            return new ArchiveValidation(
                path,
                ArchiveFormat.Unknown,
                ArchiveState.Unknown,
                capabilities,
                "The chapter content was not recognized as a supported archive format.");
        }

        if (detection.Truncated)
        {
            return new ArchiveValidation(
                path,
                detection.Format,
                ArchiveState.CorruptOrTruncated,
                capabilities,
                $"The {FormatLabel(detection.Format)} signature is present but the file is truncated.");
        }

        if (detection.Format == ArchiveFormat.Zip && !TryReadZip(path, out var zipDetail))
        {
            return new ArchiveValidation(
                path,
                ArchiveFormat.Zip,
                ArchiveState.CorruptOrTruncated,
                capabilities,
                zipDetail);
        }

        return new ArchiveValidation(
            path,
            detection.Format,
            ArchiveState.Healthy,
            capabilities,
            capabilities.Description);
    }

    private static bool TryReadZip(string path, out string detail)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                _ = entry.FullName;
            }

            detail = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or EndOfStreamException
            or IOException)
        {
            detail = $"The ZIP structure could not be read: {exception.Message}";
            return false;
        }
    }

    private static string FormatLabel(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP/CBZ",
        ArchiveFormat.Rar4 => "RAR4/CBR",
        ArchiveFormat.Rar5 => "RAR5/CBR",
        ArchiveFormat.SevenZip => "7z/CB7",
        ArchiveFormat.Pdf => "PDF",
        _ => "archive",
    };
}
