using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.Archive;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Atomic publication: a complete, validated, provenance-tagged CBZ appears at
/// the final path only after every check passes; a partial file is never
/// discoverable; and a collision is a conflict rather than an overwrite or a
/// silent "(2)".
/// </summary>
public sealed class CbzPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.Publication.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _library;
    private readonly string _staging;

    public CbzPublicationTests()
    {
        _library = Path.Combine(_root, "library");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_staging);
    }

    [Fact]
    public async Task CompleteStagingPublishesOneValidatedProvenanceTaggedCbz()
    {
        var pages = await StagePagesAsync(3);
        var target = Target("0001 - Chapter 1 [Official].cbz");

        var outcome = await new CbzChapterPublisher().PublishAsync(
            ChapterIdentity(), "Official", "sha256:test", pages, target, CancellationToken.None);

        Assert.True(outcome.Published);
        Assert.NotNull(outcome.Path);
        Assert.True(File.Exists(outcome.Path));

        var validation = new ArchiveValidator().Validate(outcome.Path!);
        Assert.Equal(ArchiveState.Healthy, validation.State);

        using var archive = ZipFile.OpenRead(outcome.Path!);
        Assert.Equal(3, archive.Entries.Count(entry =>
            !entry.FullName.Equals(CbzChapterPublisher.SourceManifestEntryName, StringComparison.Ordinal)));
        Assert.NotNull(archive.GetEntry(CbzChapterPublisher.SourceManifestEntryName));

        // No temporary artifact may survive the commit.
        Assert.Empty(Directory.GetFiles(target.FolderPath, "*.tmp"));
        Assert.Empty(Directory.GetFiles(target.FolderPath, "*.part"));
    }

    [Fact]
    public async Task MissingPageRefusesToPublishAndLeavesNoFinalFile()
    {
        var pages = await StagePagesAsync(3);
        File.Delete(pages[1].AbsolutePath);
        var target = Target("0002 - Chapter 2 [Official].cbz");

        var outcome = await new CbzChapterPublisher().PublishAsync(
            ChapterIdentity(), "Official", "sha256:test", pages, target, CancellationToken.None);

        Assert.False(outcome.Published);
        Assert.False(File.Exists(target.FilePath));
        Assert.NotNull(outcome.ConflictReason);
    }

    [Fact]
    public async Task TrailingNotFoundPagePublishesValidatedCbzWithIncompleteMetadata()
    {
        var pages = await StagePagesAsync(2);
        var target = Target("0002 - Chapter 2 [Official].cbz");
        var failures = new[]
        {
            new PageFailureEvidence(
                2,
                "https://example.test/page-3.png",
                PageFetchOutcome.NotFound,
                "HTTP 404"),
        };

        var outcome = await new CbzChapterPublisher().PublishIncompleteAsync(
            ChapterIdentity(),
            "Official",
            "sha256:test",
            pages,
            expectedPageCount: 3,
            failures,
            target,
            CancellationToken.None);

        Assert.True(outcome.Published, outcome.ConflictReason);
        Assert.NotNull(outcome.Path);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("Incomplete 2/3", StringComparison.Ordinal));

        using var archive = ZipFile.OpenRead(outcome.Path!);
        var entry = archive.GetEntry(CbzChapterPublisher.IncompleteManifestEntryName);
        Assert.NotNull(entry);
        using var stream = entry.Open();
        var metadata = JsonSerializer.Deserialize<CbzIncompleteManifest>(stream);
        Assert.NotNull(metadata);
        Assert.Equal(3, metadata.ExpectedPageCount);
        Assert.Equal(2, metadata.DownloadedPageCount);
        var missing = Assert.Single(metadata.MissingPages);
        Assert.Equal(3, missing.PageNumber);
        Assert.Equal("NotFound", missing.Outcome);
        Assert.Equal("HTTP 404", missing.Detail);

        var images = archive.Entries.Where(item =>
            item.Name.Length > 0
            && !item.FullName.StartsWith("META-INF/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, images.Length);
    }

    [Fact]
    public async Task TransientFailureCannotBePublishedAsIncomplete()
    {
        var pages = await StagePagesAsync(2);
        var target = Target("0002 - Chapter 2 [Official].cbz");

        var outcome = await new CbzChapterPublisher().PublishIncompleteAsync(
            ChapterIdentity(),
            "Official",
            "sha256:test",
            pages,
            expectedPageCount: 3,
            [new PageFailureEvidence(2, "page-3", PageFetchOutcome.NetworkFailed, "timeout")],
            target,
            CancellationToken.None);

        Assert.False(outcome.Published);
        Assert.False(File.Exists(target.FilePath));
        Assert.Contains("permanen", outcome.ConflictReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentProvenanceAtTheSamePathIsAConflictNotAnOverwrite()
    {
        var pages = await StagePagesAsync(2);
        var target = Target("0003 - Chapter 3 [Official].cbz");

        // An existing file with no Citadel provenance is never claimed.
        Directory.CreateDirectory(target.FolderPath);
        var existingBytes = await PngAsync(4);
        await File.WriteAllBytesAsync(target.FilePath, existingBytes);

        var outcome = await new CbzChapterPublisher().PublishAsync(
            ChapterIdentity(), "Official", "sha256:test", pages, target, CancellationToken.None);

        Assert.False(outcome.Published);
        Assert.NotNull(outcome.ConflictReason);
        Assert.DoesNotContain("(2)", outcome.ConflictReason, StringComparison.Ordinal);

        // The pre-existing file is untouched, byte for byte.
        Assert.Equal(existingBytes, await File.ReadAllBytesAsync(target.FilePath));
    }

    [Fact]
    public async Task SameProvenanceIsReplacedAndKeepsOneBackup()
    {
        var pages = await StagePagesAsync(2);
        var target = Target("0004 - Chapter 4 [Official].cbz");

        var first = await new CbzChapterPublisher().PublishAsync(
            ChapterIdentity(), "Official", "sha256:first", pages, target, CancellationToken.None);
        Assert.True(first.Published);

        var second = await new CbzChapterPublisher().PublishAsync(
            ChapterIdentity(), "Official", "sha256:second", pages, target, CancellationToken.None);

        Assert.True(second.Published);
        Assert.NotNull(second.BackupPath);
        Assert.True(File.Exists(second.BackupPath));
        Assert.Empty(second.ReleasedProcesses);
    }

    [Fact]
    public async Task TemporaryPublicationFileIsNotDiscoverableByTheLibraryScanner()
    {
        var titleFolder = Path.Combine(_library, "Some Title");
        Directory.CreateDirectory(titleFolder);

        // The publisher's temporary name carries no chapter extension, so a
        // crash mid-publication cannot make a partial file look like a chapter.
        File.WriteAllBytes(
            Path.Combine(titleFolder, "0005 - Chapter 5 [Official].partial." + Guid.NewGuid().ToString("N") + ".tmp"),
            await PngAsync(8));

        var titles = await new LibraryScanner().ScanAsync(_library, CancellationToken.None);

        Assert.Empty(titles);

        // A real chapter in the same folder is still discovered: the scanner
        // requires a supported archive, not merely a chapter extension.
        await WriteZipAsync(Path.Combine(titleFolder, "0005 - Chapter 5 [Official].cbz"), await PngAsync(8));
        var after = await new LibraryScanner().ScanAsync(_library, CancellationToken.None);

        Assert.Equal("Some Title", Assert.Single(after).Title);
    }

    private static async Task WriteZipAsync(string path, byte[] pageBytes)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("001.png", CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await stream.WriteAsync(pageBytes);
    }

    private DownloadTarget Target(string fileName) =>
        new(_library, "Published Title", fileName);

    private static RemoteChapterIdentity ChapterIdentity() =>
        new(
            "comix",
            new RemoteTitleIdentity("comix", "12947", "dy88", "the-novels-extra"),
            "chapter-1",
            "1",
            new RemoteGroupIdentity("comix", "9897"));

    private async Task<IReadOnlyList<StagedPage>> StagePagesAsync(int count)
    {
        var pages = new List<StagedPage>(count);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var path = Path.Combine(_staging, $"page-{ordinal:D5}.png");
            await File.WriteAllBytesAsync(path, await PngAsync(16 + ordinal));
            pages.Add(new StagedPage(ordinal, path, "png"));
        }

        return pages;
    }

    private static async Task<byte[]> PngAsync(int size)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x40;
            pixels[offset + 1] = 0x80;
            pixels[offset + 2] = 0xC0;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return await Task.FromResult(stream.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
