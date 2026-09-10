using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Auto Cover writes one <c>cover.png</c> into the deterministic title folder and
/// nothing else. Both triggers run the same command and differ only in what an
/// existing cover means; a failure is local, leaves no partial file, and can never
/// reach a chapter job, a Library refresh or Cover Builder.
/// </summary>
public sealed class AutoCoverFeatureTests : IDisposable
{
    private const string Folder = "Covered Title";
    private const string CoverUrl = "https://example.invalid/poster.jpg";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.AutoCover.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _library;
    private readonly string _staging;
    private readonly ListerFeature _lister;

    public AutoCoverFeatureTests()
    {
        _library = Path.Combine(_root, "library");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_staging);

        _lister = new ListerFeature(new LocalTitleProbe(), () => _library, (_, _) => Folder);
    }

    private string CoverPath => Path.Combine(_library, Folder, AutoCoverFeature.CoverFileName);

    private AutoCoverFeature Feature(Func<CancellationToken, Task<byte[]>> fetch) =>
        new((_, token) => fetch(token), _lister);

    [Fact]
    public async Task AFetchPublishesOneRealPngAndMayCreateTheTitleFolder()
    {
        Assert.False(Directory.Exists(Path.Combine(_library, Folder)));

        var outcome = await Feature(_ => Task.FromResult(Jpeg(24))).SaveCoverAsync(
            Candidate(), AutoCoverTrigger.Manual, confirmOverwrite: null, CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Published, outcome.Kind);
        Assert.Equal(CoverPath, outcome.Path);

        // The folder was created, the file is a genuine PNG even though the
        // provider served JPEG, and no temporary artifact survived the commit.
        Assert.True(File.Exists(CoverPath));
        Assert.True(IsPng(File.ReadAllBytes(CoverPath)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_library, Folder), "*.tmp"));
    }

    [Fact]
    public async Task AFetchUsesTheNextCoverCandidateWhenThePrimaryCannotBeDecoded()
    {
        var requested = new List<string>();
        var feature = new AutoCoverFeature(
            (url, _) =>
            {
                requested.Add(url);
                return Task.FromResult(url.EndsWith("fallback.jpg", StringComparison.Ordinal)
                    ? Jpeg(24)
                    : "<html>not an image</html>"u8.ToArray());
            },
            _lister);
        var summary = Candidate().Title with
        {
            CoverFallbackUrls = ["https://example.invalid/fallback.jpg"],
        };

        var outcome = await feature.SaveCoverAsync(
            new CoverCandidate(summary, _library, Folder),
            AutoCoverTrigger.Manual,
            confirmOverwrite: null,
            CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Published, outcome.Kind);
        Assert.Equal([CoverUrl, "https://example.invalid/fallback.jpg"], requested);
        Assert.True(IsPng(await File.ReadAllBytesAsync(CoverPath)));
    }

    [Fact]
    public async Task TheAutomaticTriggerSkipsATitleThatAlreadyExistsLocally()
    {
        await PublishChapterAsync();
        Assert.True((await _lister.ListAsync(Candidate().Title, CancellationToken.None)).TitleExists);

        var fetched = 0;
        var outcome = await Feature(_ =>
        {
            fetched++;
            return Task.FromResult(Png(16));
        }).SaveCoverAsync(Candidate(), AutoCoverTrigger.Automatic, null, CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(0, fetched);
        Assert.False(File.Exists(CoverPath));
    }

    [Fact]
    public async Task TheAutomaticTriggerSkipsAnExistingCoverInsteadOfOverwriting()
    {
        Directory.CreateDirectory(Path.Combine(_library, Folder));
        var existing = Png(12);
        await File.WriteAllBytesAsync(CoverPath, existing);

        var fetched = 0;
        var outcome = await Feature(_ =>
        {
            fetched++;
            return Task.FromResult(Png(20));
        }).SaveCoverAsync(Candidate(), AutoCoverTrigger.Automatic, null, CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(0, fetched);
        Assert.Equal(existing, await File.ReadAllBytesAsync(CoverPath));
    }

    [Fact]
    public async Task AManualFetchAsksBeforeOverwritingAndHonoursTheAnswer()
    {
        Directory.CreateDirectory(Path.Combine(_library, Folder));
        var existing = Png(12);
        await File.WriteAllBytesAsync(CoverPath, existing);

        var declined = await Feature(_ => Task.FromResult(Png(20))).SaveCoverAsync(
            Candidate(), AutoCoverTrigger.Manual, _ => false, CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Cancelled, declined.Kind);
        Assert.Equal(existing, await File.ReadAllBytesAsync(CoverPath));

        var questions = 0;
        var accepted = await Feature(_ => Task.FromResult(Jpeg(20))).SaveCoverAsync(
            Candidate(),
            AutoCoverTrigger.Manual,
            path =>
            {
                questions++;
                Assert.Equal(CoverPath, path);
                return true;
            },
            CancellationToken.None);

        Assert.Equal(1, questions);
        Assert.Equal(AutoCoverOutcomeKind.Published, accepted.Kind);
        Assert.NotEqual(existing, await File.ReadAllBytesAsync(CoverPath));
    }

    /// <summary>
    /// Neither an unreachable cover nor a payload that is not an image may leave
    /// a partial file behind, and both are reported locally rather than thrown
    /// into whatever invoked the command.
    /// </summary>
    [Fact]
    public async Task AFailedFetchOrAnInvalidPayloadLeavesNoPartialFile()
    {
        var unreachable = await Feature(_ => Task.FromException<byte[]>(new HttpRequestException("no route")))
            .SaveCoverAsync(Candidate(), AutoCoverTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Failed, unreachable.Kind);
        Assert.NotNull(unreachable.Message);
        Assert.False(File.Exists(CoverPath));

        var notAnImage = await Feature(_ => Task.FromResult("<html>blocked</html>"u8.ToArray()))
            .SaveCoverAsync(Candidate(), AutoCoverTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Failed, notAnImage.Kind);
        Assert.False(File.Exists(CoverPath));

        // A candidate with no provider cover url fails before any transport.
        var noUrl = await Feature(_ => Task.FromResult(Png(8))).SaveCoverAsync(
            new CoverCandidate(
                new RemoteTitleSummary(Title(), Folder, CoverUrl: null, LatestChapterLabel: null),
                _library,
                Folder),
            AutoCoverTrigger.Manual,
            null,
            CancellationToken.None);

        Assert.Equal(AutoCoverOutcomeKind.Failed, noUrl.Kind);
        Assert.False(Directory.Exists(Path.Combine(_library, Folder)));
    }

    /// <summary>
    /// The token a host hands an automatic invocation is what stops the cover when
    /// that host closes. Its lifetime ending mid-command is a normal cancellation,
    /// not a cover failure, and it may leave nothing behind — neither a file nor the
    /// folder the file would have been written into.
    /// </summary>
    [Fact]
    public async Task ACancelledCallerLifetimeStopsTheCoverAndLeavesNothingBehind()
    {
        using var lifetime = new CancellationTokenSource();
        var cancelled = Feature(token =>
        {
            lifetime.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Png(16));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.SaveCoverAsync(
            Candidate(), AutoCoverTrigger.Automatic, confirmOverwrite: null, lifetime.Token));

        Assert.False(File.Exists(CoverPath));
        Assert.False(Directory.Exists(Path.Combine(_library, Folder)));
    }

    private CoverCandidate Candidate() =>
        new(new RemoteTitleSummary(Title(), Folder, CoverUrl, LatestChapterLabel: null), _library, Folder);

    private static RemoteTitleIdentity Title() =>
        new("comix", "12947", "dy88", "/title/dy88-covered-title");

    private async Task PublishChapterAsync()
    {
        var folder = Path.Combine(_staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var pages = new List<StagedPage>();
        for (var ordinal = 0; ordinal < 2; ordinal++)
        {
            var path = Path.Combine(folder, $"page-{ordinal}.png");
            await File.WriteAllBytesAsync(path, Png(16));
            pages.Add(new StagedPage(ordinal, path, "png"));
        }

        var outcome = await new CbzChapterPublisher().PublishAsync(
            new RemoteChapterIdentity(
                "comix", Title(), "c1", "1", new RemoteGroupIdentity("comix", "9897")),
            "Official",
            "sha256:auto-cover-test",
            pages,
            new DownloadTarget(_library, Folder, "0001 - Chapter 1 [Official].cbz"),
            CancellationToken.None);

        Assert.True(outcome.Published, outcome.ConflictReason);
    }

    private static bool IsPng(byte[] payload) =>
        payload.Length >= 4
        && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47;

    private static byte[] Png(int size) => Encode(new PngBitmapEncoder(), size);

    private static byte[] Jpeg(int size) => Encode(new JpegBitmapEncoder(), size);

    private static byte[] Encode(BitmapEncoder encoder, int size)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x40;
            pixels[offset + 1] = 0x90;
            pixels[offset + 2] = 0xC0;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();

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
