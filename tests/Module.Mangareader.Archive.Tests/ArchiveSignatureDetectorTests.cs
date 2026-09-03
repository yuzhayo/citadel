using System.IO;
using Module.Mangareader.Archive;

namespace Module.Mangareader.Archive.Tests;

public sealed class ArchiveSignatureDetectorTests : ArchiveTestFixture
{
    private readonly ArchiveSignatureDetector _detector = new();

    private string WriteContent(string name, byte[] bytes)
    {
        var path = ChapterPath(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void DetectsZipFromContent()
    {
        var path = ChapterPath("chapter.bin");
        CreateZip(path, ("page1.png", [1, 2, 3]));

        var detection = _detector.Detect(path);

        Assert.Equal(ArchiveFormat.Zip, detection.Format);
        Assert.False(detection.Truncated);
    }

    [Fact]
    public void DetectsRar4FromContent()
    {
        var detection = _detector.Detect(WriteContent("chapter.dat", Rar4Content()));

        Assert.Equal(ArchiveFormat.Rar4, detection.Format);
        Assert.False(detection.Truncated);
    }

    [Fact]
    public void DetectsRar5FromContent()
    {
        var detection = _detector.Detect(WriteContent("chapter.dat", Rar5Content()));

        Assert.Equal(ArchiveFormat.Rar5, detection.Format);
        Assert.False(detection.Truncated);
    }

    [Fact]
    public void DetectsSevenZipFromContent()
    {
        var detection = _detector.Detect(WriteContent("chapter.dat", SevenZipContent()));

        Assert.Equal(ArchiveFormat.SevenZip, detection.Format);
        Assert.False(detection.Truncated);
    }

    [Fact]
    public void DetectsPdfFromContent()
    {
        var detection = _detector.Detect(WriteContent("chapter.dat", PdfContent()));

        Assert.Equal(ArchiveFormat.Pdf, detection.Format);
        Assert.False(detection.Truncated);
    }

    [Fact]
    public void DetectsUnknownForRandomBytes()
    {
        var detection = _detector.Detect(WriteContent("chapter.cbz", [0x00, 0xC0, 0xFF, 0xEE, 0x42]));

        Assert.Equal(ArchiveFormat.Unknown, detection.Format);
        Assert.False(detection.Recognized);
    }

    [Fact]
    public void DetectsUnknownForEmptyFile()
    {
        var detection = _detector.Detect(WriteContent("chapter.cbz", []));

        Assert.Equal(ArchiveFormat.Unknown, detection.Format);
    }

    [Fact]
    public void DetectsTruncatedZipSignature()
    {
        var path = WriteContent("chapter.cbz", [0x50, 0x4B, 0x03, 0x04, 0x00]);

        var detection = _detector.Detect(path);

        Assert.Equal(ArchiveFormat.Zip, detection.Format);
        Assert.True(detection.Truncated);
    }

    [Fact]
    public void DetectsTruncatedRar4Signature()
    {
        byte[] marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        var bytes = new byte[9];
        Array.Copy(marker, bytes, marker.Length);
        var path = WriteContent("chapter.cbr", bytes);

        var detection = _detector.Detect(path);

        Assert.Equal(ArchiveFormat.Rar4, detection.Format);
        Assert.True(detection.Truncated);
    }

    [Fact]
    public void DetectsTruncatedPdfSignature()
    {
        var path = WriteContent("chapter.pdf", "%PDF-"u8.ToArray());

        var detection = _detector.Detect(path);

        Assert.Equal(ArchiveFormat.Pdf, detection.Format);
        Assert.True(detection.Truncated);
    }

    [Fact]
    public void ExtensionMismatchFollowsContent()
    {
        // Named .cbz, but the content is a RAR5 container: content wins.
        var path = WriteContent("chapter.cbz", Rar5Content());

        var detection = _detector.Detect(path);
        var validation = new ArchiveValidator(_detector).Validate(path);

        Assert.Equal(ArchiveFormat.Rar5, detection.Format);
        Assert.Equal(ArchiveFormat.Rar5, validation.Format);
        Assert.Equal(ArchiveState.Healthy, validation.State);
        Assert.True(validation.Capabilities.Recognized);
        Assert.True(validation.Capabilities.CoverWritable);
    }
}
