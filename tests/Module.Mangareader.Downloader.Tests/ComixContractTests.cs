using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The Comix boundary: query serialization and local validation, header-driven
/// scramble handling, byte-based format detection, and which page outcomes may
/// use the single browser fallback.
/// </summary>
public sealed class ComixContractTests
{
    [Fact]
    public void TypesSerializeAsTheCapturedRepeatedKey()
    {
        var query = ComixBrowseQuery.Default with { Types = ["manhwa", "manga"] };

        var text = query.ToQueryString();

        Assert.Contains("types%5B%5D=manhwa", text, StringComparison.Ordinal);
        Assert.Contains("types%5B%5D=manga", text, StringComparison.Ordinal);
        Assert.Null(query.ValidationError);
    }

    /// <summary>
    /// The filters whose live query key was never captured must block Start with
    /// a message naming them. The previous contract sent guessed keys for these,
    /// every test passed, and the provider rejected every real request.
    /// </summary>
    [Fact]
    public void UncapturedFiltersBlockStartInsteadOfBeingGuessed()
    {
        var query = ComixBrowseQuery.Default with
        {
            Genres = ["action"],
            Statuses = ["releasing"],
            YearFrom = 2010,
            AuthorKey = "someone",
        };

        var validation = query.ValidationError;

        Assert.NotNull(validation);
        Assert.Contains("genre", validation, StringComparison.Ordinal);
        Assert.Contains("status", validation, StringComparison.Ordinal);
        Assert.Contains("release year", validation, StringComparison.Ordinal);
        Assert.Contains("author", validation, StringComparison.Ordinal);

        var text = query.ToQueryString();
        Assert.DoesNotContain("genre=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("status=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("year_from=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("author=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mode=", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(null, 1899, null)]
    [InlineData(null, null, 3000)]
    [InlineData(null, 2020, 2010)]
    public void InvalidRangesBlockStartLocally(int? minChapter, int? yearFrom, int? yearTo)
    {
        var query = ComixBrowseQuery.Default with
        {
            MinimumChapter = minChapter,
            YearFrom = yearFrom,
            YearTo = yearTo,
        };

        Assert.NotNull(query.ValidationError);
    }

    [Fact]
    public void ScrambleHeadersAreParsedOnlyWhenDeclared()
    {
        Assert.Null(ComixScrambleHeaders.Parse(null));
        Assert.Null(ComixScrambleHeaders.Parse(
            new Dictionary<string, string> { ["content-type"] = "image/webp" }));

        var header = ComixScrambleHeaders.Parse(new Dictionary<string, string>
        {
            ["x-scramble-seed"] = "3641158735",
            ["x-scramble-grid"] = "5",
            ["x-scramble-algo"] = "3",
            ["x-scramble-hash"] = "03632",
        });

        Assert.NotNull(header);
        Assert.Equal(3641158735L, header!.Seed);
        Assert.Equal(5, header.Grid);
        Assert.Equal(3, header.Algorithm);
        Assert.Equal(3632, header.Hash);
        Assert.True(ComixPageDecoder.IsSupported(header));
    }

    [Fact]
    public void UnparseableScrambleHeaderFailsVisibly()
    {
        Assert.Throws<InvalidDataException>(() => ComixScrambleHeaders.Parse(
            new Dictionary<string, string> { ["x-scramble-seed"] = "not-a-number" }));
    }

    [Fact]
    public void UnsupportedScrambleVariantIsRefusedNotPassedThrough()
    {
        var decoder = new ComixPageDecoder();
        var unknownAlgorithm = new ComixScrambleHeader(1, 5, Algorithm: 9, Hash: null);

        Assert.False(ComixPageDecoder.IsSupported(unknownAlgorithm));
        Assert.Throws<InvalidDataException>(
            () => decoder.Descramble([1, 2, 3], unknownAlgorithm));
    }

    [Fact]
    public void DescramblingReversesTheDeclaredPermutationExactly()
    {
        const int grid = 2;
        const int tilePixels = 24;
        const long seed = 3641158735L;
        var header = new ComixScrambleHeader(seed, grid, ComixPageDecoder.SupportedAlgorithm, null);
        var permutation = ComixPageDecoder.GeneratePermutation(grid, seed, null);

        var identity = new List<int>(grid * grid);
        for (var index = 0; index < grid * grid; index++) identity.Add(index);

        // Position i of the scrambled page shows tile permutation[i], which is
        // exactly what the provider sends.
        var scrambledPosition = Enumerable.Range(0, grid * grid).Select(i => permutation[i]).ToList();

        var original = Render(identity, grid, tilePixels);
        var scrambled = Render(scrambledPosition, grid, tilePixels);

        // Sanity: the scrambled layout really differs, so this is not a no-op.
        Assert.NotEqual(Pixels(original), Pixels(scrambled));

        var restored = DecodePng(new ComixPageDecoder().Descramble(EncodePng(scrambled), header));

        Assert.Equal(Pixels(original), Pixels(restored));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void PermutationIsACompleteBijectionForAnySeed(long seed)
    {
        var permutation = ComixPageDecoder.GeneratePermutation(5, seed, null);

        Assert.Equal(25, permutation.Count);
        Assert.Equal(Enumerable.Range(0, 25), permutation.OrderBy(value => value));
    }

    [Fact]
    public void FormatIsDetectedFromBytesAndUnknownPayloadIsRejected()
    {
        Assert.Equal("png", PageTransport.DetectImageFormat(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
        Assert.Equal("jpg", PageTransport.DetectImageFormat([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("webp", PageTransport.DetectImageFormat(
            [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]));

        // An HTML challenge page must never be mistaken for an image.
        var html = System.Text.Encoding.UTF8.GetBytes("<html><body>blocked</body></html>");
        Assert.Throws<InvalidDataException>(() => PageTransport.DetectImageFormat(html));
    }

    [Theory]
    [InlineData(PageFetchOutcome.NetworkFailed, true)]
    [InlineData(PageFetchOutcome.Rejected, true)]
    [InlineData(PageFetchOutcome.Challenge, true)]
    [InlineData(PageFetchOutcome.NotFound, false)]
    [InlineData(PageFetchOutcome.Throttled, false)]
    [InlineData(PageFetchOutcome.TooLarge, false)]
    [InlineData(PageFetchOutcome.Stored, false)]
    public void OnlyTheFixedOutcomesMayUseOneBrowserFallback(
        PageFetchOutcome outcome,
        bool fallbackEligible)
    {
        var result = new PageFetchResult(
            outcome,
            outcome == PageFetchOutcome.Stored ? "path" : null,
            0,
            null,
            string.Empty,
            null,
            UsedBrowserFallback: false);

        Assert.Equal(fallbackEligible, result.IsFallbackEligible);
    }

    private static Color ColorFor(int tileIndex) => Color.FromRgb(
        (byte)(20 + tileIndex * 40),
        (byte)(200 - tileIndex * 30),
        (byte)(60 + tileIndex * 20));

    /// <summary>
    /// Renders a page whose position <c>i</c> shows tile <c>tileAtPosition[i]</c>.
    /// </summary>
    private static BitmapSource Render(IReadOnlyList<int> tileAtPosition, int grid, int tilePixels)
    {
        var size = grid * tilePixels;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            for (var index = 0; index < tileAtPosition.Count; index++)
            {
                var column = index % grid;
                var row = index / grid;
                context.DrawRectangle(
                    new SolidColorBrush(ColorFor(tileAtPosition[index])),
                    null,
                    new Rect(column * tilePixels, row * tilePixels, tilePixels, tilePixels));
            }
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        return buffer;
    }
}
