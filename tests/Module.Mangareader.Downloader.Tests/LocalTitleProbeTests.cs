using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The filesystem probe both Lister and Update Checker read through: what is on
/// disk is authoritative, only the selected title folder is opened, and the
/// Citadel identity embedded by the publisher round-trips through the probe's
/// own reader.
/// </summary>
public sealed class LocalTitleProbeTests : IDisposable
{
    private const string TitleFolder = "Probed Title";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.Probe.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _library;
    private readonly string _staging;
    private readonly LocalTitleProbe _probe = new();

    public LocalTitleProbeTests()
    {
        _library = Path.Combine(_root, "library");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_staging);
    }

    [Fact]
    public async Task ANewlyPublishedChapterIsVisibleWithoutAnyLibraryRefresh()
    {
        // A second title folder proves the probe opens only the requested one.
        await PublishAsync("0001 - Chapter 1.cbz", ChapterIdentity("chapter-1", "1", "9897"), TitleFolder);
        await PublishAsync("0001 - Chapter 1.cbz", ChapterIdentity("chapter-1", "1", "9897"), "Other Title");

        var result = _probe.ProbeTitle(new LocalTitleProbeRequest(_library, TitleFolder));

        Assert.True(result.TitleExists);
        var chapter = Assert.Single(result.Chapters);
        Assert.Equal("0001 - Chapter 1.cbz", chapter.FileName);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The drift guard for the provenance contract: the publisher writes the
    /// entry and the probe reads it back through its own declared name, so the
    /// two cannot silently disagree.
    /// </summary>
    [Fact]
    public async Task TheEmbeddedCitadelIdentityRoundTripsThroughTheProbe()
    {
        Assert.Equal(CbzChapterPublisher.SourceManifestEntryName, LocalTitleProbe.SourceManifestEntryName);

        await PublishAsync(
            "0007 - Chapter 7.cbz",
            ChapterIdentity("chapter-7", "7", "4412"),
            TitleFolder);

        var chapter = Assert.Single(_probe.ProbeTitle(new LocalTitleProbeRequest(_library, TitleFolder)).Chapters);

        Assert.True(chapter.HasEmbeddedSource);
        Assert.Equal("comix", chapter.ProviderId);
        Assert.Equal("12947", chapter.ProviderTitleId);
        Assert.Equal("chapter-7", chapter.ProviderChapterId);
        Assert.Equal("7", chapter.ProviderChapterNumber);
        Assert.Equal("4412", chapter.ProviderGroupId);
        Assert.Equal("Group 4412", chapter.ProviderGroupName);
        Assert.True(chapter.IsSameChapter("comix", "chapter-7", "4412"));
    }

    [Fact]
    public async Task AManuallyDeletedChapterIsAbsentEvenThoughTheIndexStillRecordsIt()
    {
        await PublishAsync("0002 - Chapter 2.cbz", ChapterIdentity("chapter-2", "2", "9897"), TitleFolder);
        var publishedPath = Path.Combine(_library, TitleFolder, "0002 - Chapter 2.cbz");

        var index = new DownloadSourceIndex(_staging);
        Assert.True(index.TryRecordPublished(
            new DownloadJobIdentity("comix", "12947", "dy88", "chapter-2", "9897"),
            "0002 - Chapter 2.cbz",
            out _));
        Assert.NotNull(index.FindPublished(new DownloadJobIdentity("comix", "12947", "dy88", "chapter-2", "9897")));

        File.Delete(publishedPath);

        // The index is a hint; the absent file wins. The folder and the index
        // record both survive, but the title is no longer in local storage.
        var result = _probe.ProbeTitle(new LocalTitleProbeRequest(_library, TitleFolder));
        Assert.False(result.TitleExists);
        Assert.Empty(result.Chapters);
        Assert.True(Directory.Exists(Path.Combine(_library, TitleFolder)));
        Assert.NotNull(index.FindPublished(new DownloadJobIdentity("comix", "12947", "dy88", "chapter-2", "9897")));
    }

    [Fact]
    public async Task TheSameChapterNumberFromTwoGroupsStaysDistinguishable()
    {
        await PublishAsync("0003 - Chapter 3 [Official].cbz", ChapterIdentity("chapter-3a", "3", "9897"), TitleFolder);
        await PublishAsync("0003 - Chapter 3 [Scanlation].cbz", ChapterIdentity("chapter-3b", "3", "77"), TitleFolder);

        var chapters = _probe.ProbeTitle(new LocalTitleProbeRequest(_library, TitleFolder)).Chapters;

        Assert.Equal(2, chapters.Count);
        Assert.All(chapters, chapter => Assert.Equal("3", chapter.ProviderChapterNumber));
        Assert.Equal(2, chapters.Select(chapter => chapter.ProviderGroupId).Distinct(StringComparer.Ordinal).Count());

        var official = chapters.Single(chapter => chapter.ProviderGroupId == "9897");
        Assert.True(official.IsSameChapter("comix", "chapter-3a", "9897"));
        Assert.False(official.IsSameChapter("comix", "chapter-3b", "77"));
    }

    [Fact]
    public void AMissingTitleFolderIsReportedAbsent()
    {
        var result = _probe.ProbeTitle(new LocalTitleProbeRequest(_library, "Never Downloaded"));

        Assert.False(result.TitleExists);
        Assert.Empty(result.Chapters);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task AnUnreadableCandidateAndAForeignProvenanceAreNonFatalWarnings()
    {
        Directory.CreateDirectory(Path.Combine(_library, TitleFolder));
        await File.WriteAllBytesAsync(
            Path.Combine(_library, TitleFolder, "broken.cbz"),
            [0x00, 0x01, 0x02, 0x03]);
        await PublishAsync("0004 - Chapter 4.cbz", ChapterIdentity("chapter-4", "4", "9897"), TitleFolder);

        var result = _probe.ProbeTitle(new LocalTitleProbeRequest(
            _library,
            TitleFolder,
            ProviderId: "comix",
            ProviderTitleId: "999999"));

        // The readable chapter still counts; the junk file does not become one.
        Assert.True(result.TitleExists);
        var chapter = Assert.Single(result.Chapters);
        Assert.Equal("0004 - Chapter 4.cbz", chapter.FileName);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("broken.cbz", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("12947", StringComparison.Ordinal));
    }

    private static RemoteChapterIdentity ChapterIdentity(string chapterId, string number, string groupId) =>
        new(
            "comix",
            new RemoteTitleIdentity("comix", "12947", "dy88", "the-novels-extra"),
            chapterId,
            number,
            new RemoteGroupIdentity("comix", groupId));

    private async Task PublishAsync(string fileName, RemoteChapterIdentity identity, string folderName)
    {
        var pages = await StagePagesAsync(2);
        var outcome = await new CbzChapterPublisher().PublishAsync(
            identity,
            "Group " + identity.Group.GroupId,
            "sha256:probe-test",
            pages,
            new DownloadTarget(_library, folderName, fileName),
            CancellationToken.None);

        Assert.True(outcome.Published, outcome.ConflictReason);
    }

    private async Task<IReadOnlyList<StagedPage>> StagePagesAsync(int count)
    {
        var folder = Path.Combine(_staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var pages = new List<StagedPage>(count);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var path = Path.Combine(folder, $"page-{ordinal:D5}.png");
            await File.WriteAllBytesAsync(path, Png(16 + ordinal));
            pages.Add(new StagedPage(ordinal, path, "png"));
        }

        return pages;
    }

    private static byte[] Png(int size)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x30;
            pixels[offset + 1] = 0x70;
            pixels[offset + 2] = 0xB0;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
