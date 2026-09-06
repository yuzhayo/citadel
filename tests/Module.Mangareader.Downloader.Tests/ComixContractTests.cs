using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
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
    /// The 2026-09-06 live capture proved these keys, so they serialize exactly as
    /// recorded and no longer block Start. Repeated values stay repeated: one key
    /// per chosen option, never a joined list.
    /// </summary>
    [Fact]
    public void CapturedFiltersSerializeTheirRecordedRepeatedKeys()
    {
        var query = ComixBrowseQuery.Default with
        {
            SortKey = "views_7d",
            Types = ["manhwa", "manga"],
            Statuses = ["releasing", "on_hiatus"],
            Demographics = ["3", "4"],
            MinimumChapter = 10,
            YearFrom = 2010,
            YearTo = 2020,
            AuthorKey = "991",
            ArtistKey = "442",
        };

        Assert.Null(query.ValidationError);

        var text = query.ToQueryString();
        Assert.Contains("order%5Bviews_7d%5D=desc", text, StringComparison.Ordinal);
        Assert.Contains("content_rating%5B%5D=safe", text, StringComparison.Ordinal);
        Assert.Contains("content_rating%5B%5D=suggestive", text, StringComparison.Ordinal);
        Assert.Contains("types%5B%5D=manhwa", text, StringComparison.Ordinal);
        Assert.Contains("types%5B%5D=manga", text, StringComparison.Ordinal);
        Assert.Contains("statuses%5B%5D=releasing", text, StringComparison.Ordinal);
        Assert.Contains("statuses%5B%5D=on_hiatus", text, StringComparison.Ordinal);
        Assert.Contains("demographics%5B%5D=3", text, StringComparison.Ordinal);
        Assert.Contains("demographics%5B%5D=4", text, StringComparison.Ordinal);
        Assert.Contains("min_chap=10", text, StringComparison.Ordinal);
        Assert.Contains("year_from=2010", text, StringComparison.Ordinal);
        Assert.Contains("year_to=2020", text, StringComparison.Ordinal);
        Assert.Contains("authors%5B%5D=991", text, StringComparison.Ordinal);
        Assert.Contains("artists%5B%5D=442", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// All thirteen captured sorts, each serializing its own recorded order column
    /// and direction. A label is never translated into a column name.
    /// </summary>
    [Fact]
    public void EveryCapturedSortSerializesItsExactOrderField()
    {
        (string Key, string Field, string Direction)[] expected =
        [
            ("best_match", "relevance", "desc"),
            ("latest", "chapter_updated_at", "desc"),
            ("recently_added", "created_at", "desc"),
            ("title_asc", "title", "asc"),
            ("title_desc", "title", "desc"),
            ("year_desc", "year", "desc"),
            ("year_asc", "year", "asc"),
            ("score", "score", "desc"),
            ("views_7d", "views_7d", "desc"),
            ("views_30d", "views_30d", "desc"),
            ("views_90d", "views_90d", "desc"),
            ("views_total", "views_total", "desc"),
            ("follows_total", "follows_total", "desc"),
        ];

        Assert.Equal(expected.Length, ComixOptions.SortOptions.Count);

        foreach (var sort in expected)
        {
            var option = ComixOptions.FindSort(sort.Key);
            Assert.NotNull(option);
            Assert.Equal(sort.Field, option!.OrderField);
            Assert.Equal(sort.Direction, option.Direction);

            var text = (ComixBrowseQuery.Default with { SortKey = sort.Key }).ToQueryString();
            Assert.Contains(
                $"order%5B{sort.Field}%5D={sort.Direction}",
                text,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Genres and formats share one captured key and one dropdown, and their
    /// provider ids were captured live on 2026-09-06 from the taxonomy the browse
    /// page renders. Choosing them is therefore a normal query, not a refusal.
    /// </summary>
    [Fact]
    public void GenresAndFormatsSerializeTheirCapturedIdsThroughOneKey()
    {
        var query = ComixBrowseQuery.Default with
        {
            // Action and Smut are genres; 4-Koma is a format. All three are ids.
            Genres = ["6", "87268", "93164"],
            GenreMode = ComixGenreMode.Or,
        };

        Assert.Null(query.ValidationError);

        var text = query.ToQueryString();
        Assert.Contains("genres_in%5B%5D=6", text, StringComparison.Ordinal);
        Assert.Contains("genres_in%5B%5D=87268", text, StringComparison.Ordinal);
        Assert.Contains("genres_in%5B%5D=93164", text, StringComparison.Ordinal);
        Assert.Contains("genres_mode=or", text, StringComparison.Ordinal);

        // The match mode is only meaningful with a selection, so it is omitted
        // rather than sent as a stray default.
        Assert.DoesNotContain(
            "genres_mode",
            ComixBrowseQuery.Default.ToQueryString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the values captured live from the provider's own rendered taxonomy.
    /// Demographic ids are integers and are not in label order, which is exactly
    /// what an inference from the labels got wrong.
    /// </summary>
    [Fact]
    public void TheCapturedOptionIdsMatchTheLiveProviderTaxonomy()
    {
        Assert.Equal(
            [("3", "Josei"), ("4", "Seinen"), ("1", "Shoujo"), ("2", "Shounen")],
            ComixOptions.Demographics.Select(option => (option.Key, option.DisplayName)));

        Assert.Equal(31, ComixOptions.Genres.Count);
        Assert.Equal(9, ComixOptions.Formats.Count);

        Assert.Equal("6", IdOf(ComixOptions.Genres, "Action"));
        Assert.Equal("87264", IdOf(ComixOptions.Genres, "Adult"));
        Assert.Equal("87266", IdOf(ComixOptions.Genres, "Hentai"));
        Assert.Equal("87267", IdOf(ComixOptions.Genres, "Mature"));
        Assert.Equal("87268", IdOf(ComixOptions.Genres, "Smut"));
        Assert.Equal("40", IdOf(ComixOptions.Genres, "Harem"));
        Assert.Equal("30", IdOf(ComixOptions.Genres, "Wuxia"));

        Assert.Equal("93164", IdOf(ComixOptions.Formats, "4-Koma"));
        Assert.Equal("93172", IdOf(ComixOptions.Formats, "Full Color"));
        Assert.Equal("93171", IdOf(ComixOptions.Formats, "Web Comic"));

        // Genres and formats feed one key, so their ids must not collide.
        var genreIds = ComixOptions.Genres.Select(option => option.Key);
        var formatIds = ComixOptions.Formats.Select(option => option.Key);
        Assert.Empty(genreIds.Intersect(formatIds, StringComparer.Ordinal));

        // A sort outside the captured table is still refused, never defaulted.
        Assert.NotNull((ComixBrowseQuery.Default with { SortKey = "invented" }).ValidationError);
    }

    private static string IdOf(IReadOnlyList<RemoteOption> options, string label) =>
        options.Single(option => option.DisplayName == label).Key;

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
