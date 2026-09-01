using Module.Mangareader.Archive;

namespace Module.Mangareader.Archive.Tests;

public sealed class CoverArchiveWriterTests : ArchiveTestFixture
{
    [Fact]
    public void ValidateCoverPngAcceptsValidPng()
    {
        CoverArchiveWriter.ValidateCoverPng(MinimalPng());
    }

    [Fact]
    public void ValidateCoverPngRejectsEmptyBytes()
    {
        Assert.Throws<InvalidDataException>(() => CoverArchiveWriter.ValidateCoverPng([]));
    }

    [Fact]
    public void ValidateCoverPngRejectsGarbageBytes()
    {
        Assert.Throws<InvalidDataException>(
            () => CoverArchiveWriter.ValidateCoverPng([0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x00]));
    }

    [Fact]
    public void ValidateCoverPngRejectsTruncatedPng()
    {
        var truncated = MinimalPng()[..^6];

        Assert.Throws<InvalidDataException>(() => CoverArchiveWriter.ValidateCoverPng(truncated));
    }

    [Fact]
    public void ValidateCoverPngRejectsMissingIend()
    {
        var png = MinimalPng();
        // Corrupt the IEND chunk type so the walk never finds a terminator.
        png[^8] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => CoverArchiveWriter.ValidateCoverPng(png));
    }

    [Fact]
    public void ValidateBakedZipRejectsCorruptedPageBytes()
    {
        var source = ChapterPath("source.cbz");
        byte[] pageOne = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] pageTwo = [9, 9, 8, 8, 7, 7];
        CreateZip(source, ("001.png", pageOne), ("pages/002.png", pageTwo));

        var baked = ChapterPath("baked.cbz");
        var manifest = CoverArchiveWriter.WriteBakedZip(
            source,
            baked,
            MinimalPng(),
            CancellationToken.None);

        // Same layout, but one page's bytes are corrupted.
        byte[] corruptedPage = [1, 2, 3, 4, 0xFF, 6, 7, 8];
        var tampered = ChapterPath("tampered.cbz");
        CreateZip(
            tampered,
            (CoverArchiveWriter.GeneratedCoverEntryName, MinimalPng()),
            ("001.png", corruptedPage),
            ("pages/002.png", pageTwo));

        var exception = Assert.Throws<InvalidDataException>(() =>
            CoverArchiveWriter.ValidateBakedZip(
                tampered,
                MinimalPng(),
                manifest,
                CancellationToken.None));
        Assert.Contains("001.png", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateBakedZipRejectsResizedPage()
    {
        var source = ChapterPath("source.cbz");
        byte[] pageOne = [1, 2, 3, 4, 5, 6, 7, 8];
        CreateZip(source, ("001.png", pageOne));

        var baked = ChapterPath("baked.cbz");
        var manifest = CoverArchiveWriter.WriteBakedZip(
            source,
            baked,
            MinimalPng(),
            CancellationToken.None);

        // Same name, different size.
        byte[] shorterPage = [1, 2, 3];
        var tampered = ChapterPath("tampered.cbz");
        CreateZip(
            tampered,
            (CoverArchiveWriter.GeneratedCoverEntryName, MinimalPng()),
            ("001.png", shorterPage));

        var exception = Assert.Throws<InvalidDataException>(() =>
            CoverArchiveWriter.ValidateBakedZip(
                tampered,
                MinimalPng(),
                manifest,
                CancellationToken.None));
        Assert.Contains("size", exception.Message, StringComparison.Ordinal);
    }
}
