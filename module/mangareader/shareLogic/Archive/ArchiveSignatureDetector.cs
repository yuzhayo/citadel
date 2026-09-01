using System.IO;

namespace Module.Mangareader.Archive;

/// <summary>
/// The result of inspecting a file's leading bytes. <see cref="Truncated"/>
/// marks content that carries a complete, recognized signature but ends
/// before the smallest plausible container of that format. Files that end
/// inside a signature cannot be attributed to a format with confidence and
/// are reported as <see cref="ArchiveFormat.Unknown"/>.
/// </summary>
public sealed record SignatureDetection(ArchiveFormat Format, bool Truncated)
{
    public bool Recognized => Format != ArchiveFormat.Unknown;
}

/// <summary>
/// Detects the archive container from file content signatures, never from
/// the extension alone. No external dependency is needed: every supported
/// container starts with a fixed magic sequence.
/// </summary>
public sealed class ArchiveSignatureDetector
{
    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmptyArchive = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] ZipSpannedArchive = [0x50, 0x4B, 0x07, 0x08];
    private static readonly byte[] Rar4Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] PdfHeader = [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'];

    private const int MaxSignatureLength = 8;

    /// <summary>
    /// The smallest byte count below which a file with a complete signature
    /// cannot possibly hold a valid container of that format.
    /// </summary>
    private static readonly IReadOnlyDictionary<ArchiveFormat, long> MinimumContainerSizes =
        new Dictionary<ArchiveFormat, long>
        {
            // Empty ZIP: one end-of-central-directory record.
            [ArchiveFormat.Zip] = 22,
            // RAR4: marker + smallest archive header + end-of-archive block.
            [ArchiveFormat.Rar4] = 21,
            // RAR5: marker + smallest main archive header.
            [ArchiveFormat.Rar5] = 22,
            // 7z: 32-byte signature/version/start-header.
            [ArchiveFormat.SevenZip] = 32,
            // PDF: "%PDF-x.y" version banner.
            [ArchiveFormat.Pdf] = 8,
        };

    public SignatureDetection Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The archive was not found.", path);

        Span<byte> header = stackalloc byte[MaxSignatureLength];
        int headerLength;
        long fileLength;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            fileLength = stream.Length;
            headerLength = stream.Read(header);
        }

        header = header[..headerLength];
        var format = MatchSignature(header);
        if (format == ArchiveFormat.Unknown)
            return new SignatureDetection(ArchiveFormat.Unknown, false);

        var truncated = fileLength < MinimumContainerSizes[format];
        return new SignatureDetection(format, truncated);
    }

    private static ArchiveFormat MatchSignature(ReadOnlySpan<byte> header)
    {
        // RAR5 is checked before RAR4: the RAR4 marker is an exact prefix of
        // the RAR5 marker, so the longer signature must win.
        if (header.StartsWith(Rar5Marker)) return ArchiveFormat.Rar5;
        if (header.StartsWith(Rar4Marker)) return ArchiveFormat.Rar4;
        if (header.StartsWith(SevenZipSignature)) return ArchiveFormat.SevenZip;
        if (header.StartsWith(ZipLocalHeader)
            || header.StartsWith(ZipEmptyArchive)
            || header.StartsWith(ZipSpannedArchive))
        {
            return ArchiveFormat.Zip;
        }
        if (header.StartsWith(PdfHeader)) return ArchiveFormat.Pdf;
        return ArchiveFormat.Unknown;
    }
}
