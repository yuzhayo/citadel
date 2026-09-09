using System.IO;
using System.Text.Json.Nodes;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Sources.Comix;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Comix snapshot-export boundary: partition offer, captured single-rating
/// query mapping, strict item parsing with the placeholder rule, and the typed
/// exact-page 401/403 retry. Fixtures and a fake release/fetch pair only — no
/// full crawl, no browser launch, no stored secrets, no UI.
/// </summary>
public sealed class CatalogSnapshotSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.CatalogMirror.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotPartitionsAreTheFourOrderedRatings()
    {
        var source = Source();

        var keys = source.SnapshotPartitions.Select(partition => partition.Key).ToArray();

        Assert.Equal(["safe", "suggestive", "erotica", "pornographic"], keys);
        Assert.Equal("comix", ((ICatalogSnapshotSource)source).SourceId);
    }

    [Fact]
    public void LatestSortSerializesCapturedUpdateColumn()
    {
        var query = new ComixBrowseQuery { SortKey = "latest", Ratings = ["safe"] };

        Assert.Null(query.ValidationError);
        var text = query.ToQueryString();

        Assert.Contains("order%5Bchapter_updated_at%5D=desc", text, StringComparison.Ordinal);
        Assert.Contains("content_rating%5B%5D=safe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PartitionQueryCarriesSingleRatingAndTitleSortOnly()
    {
        var query = new ComixBrowseQuery { SortKey = "title_asc", Ratings = ["erotica"] };

        Assert.Null(query.ValidationError);
        var text = query.ToQueryString();

        Assert.Contains("order%5Btitle%5D=asc", text, StringComparison.Ordinal);
        Assert.Contains("content_rating%5B%5D=erotica", text, StringComparison.Ordinal);
        Assert.DoesNotContain("suggestive", text, StringComparison.Ordinal);
        Assert.DoesNotContain("genres_in", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NonObjectItemFailsTheWholePage()
    {
        var result = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["hid"] = "h1", ["title"] = "Fine" },
                JsonValue.Create(42)),
            ["meta"] = new JsonObject { ["total"] = 2L, ["hasNext"] = false },
        };

        var exception = Assert.Throws<ComixContractException>(() =>
            ComixSource.ReadSnapshotPage(result, 1, "safe", DateTimeOffset.UtcNow));
        Assert.Contains("not an object", exception.Message);
    }

    [Fact]
    public void SnapshotItemMapsCapturedFields()
    {
        var entry = new JsonObject
        {
            ["id"] = 7L,
            ["hid"] = "dy88",
            ["title"] = "Mapped Title",
            ["altTitles"] = new JsonArray("Alt One", " ", "Alt Two"),
            ["url"] = "/title/dy88-mapped-title",
            ["poster"] = new JsonObject { ["medium"] = "https://comix.ws/covers/m.jpg" },
            ["latestChapter"] = 143L,
            ["type"] = "manhwa",
            ["status"] = "releasing",
            ["originalLanguage"] = "ko",
            ["year"] = 2021L,
            ["synopsis"] = "A synopsis.",
        };
        var capturedAt = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

        var item = ComixSource.ReadSnapshotItem(entry, "safe", capturedAt);

        Assert.Equal("comix", item.SourceId);
        Assert.Equal("7", item.TitleId);
        Assert.Equal("dy88", item.TitleHid);
        Assert.Equal("https://comix.ws/title/dy88-mapped-title", item.CanonicalUrl);
        Assert.Equal("Mapped Title", item.Title);
        Assert.Equal(["Alt One", "Alt Two"], item.AlternateTitles);
        Assert.Equal("https://comix.ws/covers/m.jpg", item.CoverUrl);
        Assert.Equal(143, item.LatestChapterValue);
        Assert.Equal("Ch. 143", item.LatestChapterLabel);
        Assert.Equal("safe", item.Rating);
        Assert.Equal("manhwa", item.Type);
        Assert.Equal("releasing", item.Status);
        Assert.Equal("ko", item.Language);
        Assert.Equal(2021, item.Year);
        Assert.Equal("A synopsis.", item.Synopsis);
        Assert.Equal(capturedAt, item.CapturedAtUtc);
        Assert.False(item.IsTitlePlaceholder);
    }

    [Fact]
    public void MissingTitleBecomesPlaceholderWhileMissingIdentityThrows()
    {
        var withoutTitle = new JsonObject { ["hid"] = "zz1", ["id"] = 9 };
        var item = ComixSource.ReadSnapshotItem(withoutTitle, "erotica", DateTimeOffset.UtcNow);

        Assert.Equal("(Untitled zz1)", item.Title);
        Assert.True(item.IsTitlePlaceholder);
        Assert.Equal("zz1", item.TitleHid);

        var blankTitle = new JsonObject { ["hid"] = "zz2", ["title"] = "  " };
        Assert.True(ComixSource.ReadSnapshotItem(blankTitle, "erotica", DateTimeOffset.UtcNow)
            .IsTitlePlaceholder);

        var withoutHid = new JsonObject { ["title"] = "No identity" };
        Assert.Throws<ComixContractException>(
            () => ComixSource.ReadSnapshotItem(withoutHid, "safe", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SessionExpiryRetriesTheExactPageOnce()
    {
        var calls = 0;
        Task<CatalogSnapshotPage> Fetch(CancellationToken token)
        {
            calls++;
            if (calls == 1)
            {
                throw new ComixContractException("expired") { HttpStatus = 401 };
            }

            return Task.FromResult(new CatalogSnapshotPage([], 0, 1, false));
        }

        var releases = 0;
        Task Release(CancellationToken _) { releases++; return Task.CompletedTask; }

        var page = await ComixSource.WithSnapshotSessionRetryAsync(
            Fetch, Release, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(1, releases);
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public async Task RetryHappensOnlyForTypedSessionExpiry()
    {
        foreach (var status in new int?[] { null, 500, 200 })
        {
            var calls = 0;
            Task<CatalogSnapshotPage> Fetch(CancellationToken _)
            {
                calls++;
                throw new ComixContractException("failed") { HttpStatus = status };
            }

            var releases = 0;
            Task Release(CancellationToken _) { releases++; return Task.CompletedTask; }

            await Assert.ThrowsAsync<ComixContractException>(() =>
                ComixSource.WithSnapshotSessionRetryAsync(Fetch, Release, CancellationToken.None));
            Assert.Equal(1, calls);
            Assert.Equal(0, releases);
        }
    }

    [Fact]
    public async Task PersistentExpiryPropagatesAfterOneRetry()
    {
        var calls = 0;
        Task<CatalogSnapshotPage> Fetch(CancellationToken _)
        {
            calls++;
            throw new ComixContractException("expired") { HttpStatus = 403 };
        }

        var releases = 0;
        Task Release(CancellationToken _) { releases++; return Task.CompletedTask; }

        await Assert.ThrowsAsync<ComixContractException>(() =>
            ComixSource.WithSnapshotSessionRetryAsync(Fetch, Release, CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(1, releases);
    }

    private ComixSource Source()
    {
        var client = new DownloaderPyHostClient(
            Path.Combine(_root, Guid.NewGuid().ToString("N")));
        _disposables.Add(client);
        return new ComixSource(client);
    }
}
