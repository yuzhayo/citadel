namespace Module.Mangareader.Archive;

/// <summary>
/// What Citadel may do with a recognized archive container. Detection and
/// writing are deliberately separate: recognizing a RAR or PDF chapter never
/// implies Citadel can rewrite it, and nothing is ever silently converted
/// into another container.
/// </summary>
public sealed record ArchiveCapabilities(
    ArchiveFormat Format,
    bool Recognized,
    bool CoverWritable,
    string Description)
{
    public static ArchiveCapabilities For(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => new ArchiveCapabilities(
            format,
            true,
            true,
            "ZIP/CBZ chapters can be read and rewritten."),
        ArchiveFormat.Rar4 => new ArchiveCapabilities(
            format,
            true,
            false,
            "RAR4/CBR chapters are recognized, but only ZIP/CBZ chapters can be rewritten."),
        ArchiveFormat.Rar5 => new ArchiveCapabilities(
            format,
            true,
            false,
            "RAR5/CBR chapters are recognized, but only ZIP/CBZ chapters can be rewritten."),
        ArchiveFormat.SevenZip => new ArchiveCapabilities(
            format,
            true,
            false,
            "7z/CB7 chapters are recognized, but only ZIP/CBZ chapters can be rewritten."),
        ArchiveFormat.Pdf => new ArchiveCapabilities(
            format,
            true,
            false,
            "PDF chapters are recognized, but only ZIP/CBZ chapters can be rewritten."),
        _ => new ArchiveCapabilities(
            format,
            false,
            false,
            "The chapter content was not recognized as a supported archive format."),
    };
}
