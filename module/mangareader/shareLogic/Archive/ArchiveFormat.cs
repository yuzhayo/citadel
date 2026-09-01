namespace Module.Mangareader.Archive;

/// <summary>
/// Archive containers recognized from content signatures. Recognition only
/// names the container; whether Citadel may rewrite it is decided by
/// <see cref="ArchiveCapabilities"/>.
/// </summary>
public enum ArchiveFormat
{
    Unknown,
    Zip,
    Rar4,
    Rar5,
    SevenZip,
    Pdf,
}
