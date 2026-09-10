using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The Catalog boundary as the production smoke proved it: the plugin calls the
/// page's configured Axios client, and the client's interceptor both signs the
/// request with <c>_</c> and unwraps the envelope, so what reaches C# is already
/// the provider payload root <c>{items, meta}</c>.
///
/// Every test here runs the same two production members the adapter runs —
/// <see cref="ComixSource.ReadApiResponse"/> then
/// <see cref="ComixSource.ReadCatalogPage"/> — so a raw envelope can never
/// decode again without failing this file.
/// </summary>
public sealed class ComixCapturedContractTests
{
    private const string CapturedQuery =
        "order%5Bchapter_updated_at%5D=desc"
        + "&page=1&limit=28"
        + "&content_rating%5B%5D=safe"
        + "&content_rating%5B%5D=suggestive"
        + "&content_rating%5B%5D=erotica"
        + "&content_rating%5B%5D=pornographic";

    /// <summary>
    /// The bridge's own output shape, transcribed from the production smoke and
    /// trimmed to two items with a shortened synopsis. The meta block carries the
    /// live values (total 71993, lastPage 2572).
    /// </summary>
    private const string BridgePayload = """
    {
      "items": [
        {
          "id": 3750,
          "hid": "exnx",
          "title": "Murim's Youngest Miracle Demon Doctor",
          "altTitles": ["Murim's Youngest Miracle Demon Doctor", "\uc2e0\ub9c8\uc758\uc120"],
          "type": "manhwa",
          "status": "releasing",
          "originalLanguage": "ko",
          "poster": {
            "medium": "https://static.comix.ws/aac8/i/7/55/68e116d7407a6@280.jpg",
            "large": "https://static.comix.ws/aac8/i/7/55/68e116d7407a6.jpg"
          },
          "latestChapter": 90,
          "finalChapter": 0,
          "finalVolume": 0,
          "hasChapters": true,
          "chapterUpdatedAtFormatted": "2m ago",
          "startDate": "2025",
          "endDate": "?",
          "year": 2025,
          "rank": 1632,
          "synopsis": "In the world of Murim, there are two legendary titles…"
        },
        {
          "id": 4102,
          "hid": "60qg",
          "title": "The Red Shirt",
          "type": "manga",
          "status": "finished",
          "poster": { "medium": "https://static.comix.ws/aac8/i/4/10/red@280.jpg" },
          "latestChapter": 12,
          "hasChapters": true,
          "year": 2024
        }
      ],
      "meta": {
        "total": 71993,
        "perPage": 28,
        "page": 1,
        "lastPage": 2572,
        "from": 1,
        "to": 28,
        "hasNext": true,
        "hasPrev": false
      }
    }
    """;

    /// <summary>What <c>downloader.api</c> returns to C# for one call.</summary>
    private static JsonObject BridgeResponse(string json, int status = 200) => new()
    {
        ["status"] = status,
        ["content_type"] = "application/json",
        ["final_url"] = ComixContract.BaseUrl + ComixContract.RouteBrowse,
        ["json"] = JsonNode.Parse(json),
        ["is_json"] = true,
        ["text_head"] = json.Length <= 512 ? json : json[..512],
    };

    private static RemoteCatalogPage Decode(string json, int status = 200, int page = 1) =>
        ComixSource.ReadCatalogPage(
            ComixSource.ReadApiResponse(BridgeResponse(json, status), ComixContract.RouteBrowse),
            page);

    /// <summary>
    /// The captured <c>url</c> is the provider's own title page path. It has to leave
    /// the adapter already absolute: the consumer that stores it as provenance is
    /// provider-neutral and may not resolve a Comix route itself, so a relative value
    /// silently produced an empty canonical url in every binding.
    /// </summary>
    [Fact]
    public void ACapturedTitlePathLeavesTheAdapterAsAnAbsoluteUrl()
    {
        var page = Decode("""
        {
          "items": [
            { "id": 3750, "hid": "exnx", "title": "Relative", "url": "/title/exnx-relative" },
            { "id": 4102, "hid": "60qg", "title": "Absolute", "url": "https://comix.ws/title/60qg-absolute" },
            { "id": 4103, "hid": "9zzz", "title": "Other scheme", "url": "javascript:alert(1)" },
            { "id": 4104, "hid": "8yyy", "title": "Missing" },
            { "id": 4105, "hid": "7xxx", "title": "Protocol relative", "url": "//evil.example/title/7xxx-x" },
            { "id": 4106, "hid": "6www", "title": "Other path", "url": "/browse/6www" },
            { "id": 4107, "hid": "5vvv", "title": "Foreign host", "url": "https://evil.example/title/5vvv-v" },
            { "id": 4108, "hid": "4uuu", "title": "Right host wrong path", "url": "https://comix.ws/browse/4uuu" }
          ],
          "meta": { "total": 8, "page": 1, "hasNext": false }
        }
        """);

        Assert.Equal(8, page.Items.Count);
        Assert.Equal("https://comix.ws/title/exnx-relative", page.Items[0].Identity.Slug);

        // A value the provider already supplied as absolute is kept, not re-derived.
        Assert.Equal("https://comix.ws/title/60qg-absolute", page.Items[1].Identity.Slug);

        // Anything unusable stays empty so the consumer's existing fallback shows
        // instead of a broken link. That includes the two relative forms that must
        // not be resolved — a protocol-relative value would become another origin,
        // and only the captured title page path is this provider's title url — and
        // the two absolute forms that are well formed but are not this provider's
        // title page. Being parseable is not evidence of provenance.
        Assert.Equal(string.Empty, page.Items[2].Identity.Slug);
        Assert.Equal(string.Empty, page.Items[3].Identity.Slug);
        Assert.Equal(string.Empty, page.Items[4].Identity.Slug);
        Assert.Equal(string.Empty, page.Items[5].Identity.Slug);
        Assert.Equal(string.Empty, page.Items[6].Identity.Slug);
        Assert.Equal(string.Empty, page.Items[7].Identity.Slug);
    }

    [Fact]
    public void TheDefaultFilterExplicitlyIncludesEveryCapturedRating()
    {
        Assert.Equal(28, ComixContract.PageSize);
        Assert.Equal("https://comix.ws", ComixContract.BaseUrl);
        Assert.Equal("/api/v1/manga", ComixContract.RouteBrowse);

        var wire = ComixBrowseQuery.Default.ToQueryString()
            + "&page=1&limit=" + ComixContract.PageSize.ToString(CultureInfo.InvariantCulture);

        // Parameter order carries no meaning, so the expected application
        // default and serialized wire form are compared as sets of pairs.
        Assert.Equal(SortedPairs(CapturedQuery), SortedPairs(wire));
    }

    private static IEnumerable<string> SortedPairs(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);

    [Fact]
    public void TheBridgePayloadDecodesIntoCatalogItems()
    {
        var catalog = Decode(BridgePayload);

        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal(71993, catalog.Total);
        Assert.Equal(1, catalog.Page);
        Assert.True(catalog.HasMore);

        var first = catalog.Items[0];
        Assert.Equal("exnx", first.Identity.TitleHid);
        Assert.Equal("3750", first.Identity.TitleId);
        Assert.Equal("comix", first.Identity.SourceId);
        Assert.Equal("Murim's Youngest Miracle Demon Doctor", first.DisplayName);
        Assert.Equal("https://static.comix.ws/aac8/i/7/55/68e116d7407a6@280.jpg", first.CoverUrl);
        Assert.Equal("Ch. 90", first.LatestChapterLabel);

        // The second item has no large poster, so medium is what a card shows.
        Assert.Equal("https://static.comix.ws/aac8/i/4/10/red@280.jpg", catalog.Items[1].CoverUrl);
    }

    /// <summary>
    /// The regression guard for the double unwrap: the envelope the site answers
    /// with on the wire is already gone by the time C# sees it, so a payload that
    /// still carries <c>status</c>/<c>result</c> has no <c>items</c> at its root
    /// and must fail instead of decoding.
    /// </summary>
    [Fact]
    public void ARawEnvelopeIsRefusedBecauseTheClientAlreadyUnwrappedIt()
    {
        var rawEnvelope = "{\"status\":\"ok\",\"result\":" + BridgePayload + "}";

        var exception = Assert.Throws<ComixContractException>(() => Decode(rawEnvelope));

        Assert.Contains("items", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HasMoreComesFromTheProviderMetaNotFromTheItemCount()
    {
        var payload = JsonNode.Parse(BridgePayload)!.AsObject();
        payload["meta"]!.AsObject()["hasNext"] = false;

        var catalog = Decode(payload.ToJsonString(), page: 2572);

        // Two items on the last page: the item count alone would have claimed
        // there is more, the provider's own verdict says there is not.
        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal(2572, catalog.Page);
        Assert.False(catalog.HasMore);
    }

    [Fact]
    public void AnItemWithoutIdentityOrTitleIsAContractFailureNotAnEmptyCard()
    {
        Assert.Throws<ComixContractException>(
            () => Decode("""{"items":[{"year":2024}],"meta":{"total":1,"hasNext":false}}"""));
    }

    [Fact]
    public void ARejectedRequestIsReportedWithItsHttpStatus()
    {
        var exception = Assert.Throws<ComixContractException>(
            () => Decode("""{"message":"Missing token."}""", status: 403));

        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonSuccessStatusIsReportedNotSwallowed()
    {
        Assert.Throws<ComixContractException>(
            () => Decode("""{"message":"Server error."}""", status: 500));
    }

    [Fact]
    public void ANonJsonAnswerIsAContractFailure()
    {
        var response = new JsonObject
        {
            ["status"] = 200,
            ["content_type"] = "text/html",
            ["final_url"] = ComixContract.BaseUrl,
            ["json"] = null,
            ["is_json"] = false,
            ["text_head"] = "<html>",
        };

        Assert.Throws<ComixContractException>(
            () => ComixSource.ReadApiResponse(response, ComixContract.RouteBrowse));
    }

    /// <summary>
    /// Genre and format lookups still have no captured endpoint, so they are not
    /// advertised as lookup kinds at all — the filter panel serves both from the
    /// captured static taxonomy. The refusal stays as the backstop for a caller that
    /// ignores the capability list: it happens before any browser session is started,
    /// rather than against a guessed route.
    /// </summary>
    [Theory]
    [InlineData(RemoteLookupKind.Genre)]
    [InlineData(RemoteLookupKind.Format)]
    public async Task AnUncapturedLookupRefusesVisibly(RemoteLookupKind kind)
    {
        var staging = Path.Combine(Path.GetTempPath(), "comix-refusals-" + Guid.NewGuid().ToString("N"));
        using var client = new DownloaderPyHostClient(staging);
        var source = new ComixSource(client);

        var exception = await Assert.ThrowsAsync<ComixContractException>(
            () => source.LookupAsync(kind, "action", default));

        Assert.Contains("endpoint live", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The advertised lookups are exactly the two the captured <c>tags/search</c>
    /// route can answer, so a consumer that builds its controls from the capability
    /// list never offers one that can only refuse.
    /// </summary>
    [Fact]
    public void TheAdvertisedLookupKindsAreTheCapturedOnes()
    {
        var staging = Path.Combine(Path.GetTempPath(), "comix-capabilities-" + Guid.NewGuid().ToString("N"));
        using var client = new DownloaderPyHostClient(staging);

        Assert.Equal(
            [RemoteLookupKind.Author, RemoteLookupKind.Artist],
            new ComixSource(client).Capabilities.LookupKinds);
    }

    /// <summary>
    /// Author and artist use the captured <c>tags/search</c> route, whose entries
    /// carry the same <c>{id, title, slug}</c> triple as every other Comix option
    /// list. Only the provider id may enter a browse query.
    /// </summary>
    [Fact]
    public void ACapturedTagLookupDecodesIntoProviderIds()
    {
        // Parsed, not constructed: the bridge hands C# a parsed payload, and only
        // a parsed numeric node decodes the way the adapter reads it.
        var payload = JsonNode.Parse("""
        {
          "items": [
            { "id": 991, "title": "Someone", "slug": "someone" },
            { "title": "No id" },
            { "id": 442, "title": "Artist Two", "slug": "artist-two" }
          ]
        }
        """)!.AsObject();

        var options = ComixSource.ReadLookupOptions(payload);

        Assert.Equal(2, options.Count);
        Assert.Equal("991", options[0].Key);
        Assert.Equal("Someone", options[0].DisplayName);
        Assert.Equal("442", options[1].Key);

        // A payload without the captured container is a contract failure.
        Assert.Throws<ComixContractException>(
            () => ComixSource.ReadLookupOptions(new JsonObject { ["data"] = new JsonArray() }));
    }
}
