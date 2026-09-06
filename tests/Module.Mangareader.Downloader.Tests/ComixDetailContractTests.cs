using System.Text.Json.Nodes;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The detail, group, chapter and manifest contracts, frozen from payloads
/// captured live on 2026-09-05 through the page's own Axios client (which is what
/// decrypts them — the raw network body for chapters arrives as <c>{"e":…}</c>).
/// Each test is one real contract, transcribed and trimmed, not an assumed shape.
/// </summary>
public sealed class ComixDetailContractTests
{
    private static readonly RemoteTitleIdentity Title = new(
        "comix", "119397", "50xj7", "/title/50xj7-lucky-bitch");

    /// <summary>Captured <c>GET /api/v1/manga/50xj7</c>, trimmed.</summary>
    private const string DetailPayload = """
    {
      "id": 119397,
      "hid": "50xj7",
      "title": "Lucky Bitch",
      "altTitles": ["Lucky Beach", "\ub7ed\ud0a4\ube44\uce58"],
      "type": "manhwa",
      "status": "releasing",
      "originalLanguage": "ko",
      "year": 2026,
      "contentRating": "safe",
      "poster": {
        "medium": "https://static.comix.ws/9e8f/i/a/97/6a57b776ce3ac@280.jpg",
        "large": "https://static.comix.ws/9e8f/i/a/97/6a57b776ce3ac.jpg"
      },
      "latestChapter": 20,
      "hasChapters": true,
      "synopsis": "Jeong-im, a girl raised in a remote seaside village, finally snaps.",
      "url": "/title/50xj7-lucky-bitch",
      "firstChapterUrl": "/title/50xj7-lucky-bitch/10913444-chapter-1",
      "latestChapterUrl": "/title/50xj7-lucky-bitch/11312240-chapter-20",
      "genres": [
        { "id": 11, "title": "Drama", "slug": "drama" },
        { "id": 87267, "title": "Mature", "slug": "mature" }
      ],
      "demographics": [{ "id": 3, "title": "Josei", "slug": "josei" }],
      "formats": [{ "id": 93170, "title": "Long Strip", "slug": "long-strip" }],
      "tags": [],
      "authors": [
        { "id": 1108, "title": "MAJOR", "slug": "major" },
        { "id": 7704, "title": "Yaksu", "slug": "yaksu" }
      ],
      "artists": [{ "id": 7705, "title": "Yaksu", "slug": "yaksu" }]
    }
    """;

    /// <summary>
    /// Captured <c>GET /api/v1/manga/50xj7/chapters?order[number]=desc</c>: two
    /// groups that both published chapter 19, which is exactly the case where
    /// mixing them would silently replace a translation.
    /// </summary>
    private const string ChaptersPayload = """
    {
      "items": [
        {
          "id": 11312240, "mangaId": 119397, "number": 20, "volume": 0, "name": "",
          "language": "en", "isOfficial": false, "groupId": 1387,
          "group": { "id": 1387, "name": "DivaScans" },
          "url": "/title/50xj7-lucky-bitch/11312240-chapter-20"
        },
        {
          "id": 11302252, "mangaId": 119397, "number": 19, "volume": 0, "name": "",
          "language": "en", "isOfficial": false, "groupId": 1387,
          "group": { "id": 1387, "name": "DivaScans" },
          "url": "/title/50xj7-lucky-bitch/11302252-chapter-19"
        },
        {
          "id": 11308257, "mangaId": 119397, "number": 19, "volume": 0, "name": "",
          "language": "en", "isOfficial": false, "groupId": 9303,
          "group": { "id": 9303, "name": "Luna Toons" },
          "url": "/title/50xj7-lucky-bitch/11308257-chapter-19"
        },
        {
          "id": 11288270, "mangaId": 119397, "number": 18, "volume": 0, "name": "",
          "language": "en", "isOfficial": false, "groupId": 9303,
          "group": { "id": 9303, "name": "Luna Toons" },
          "url": "/title/50xj7-lucky-bitch/11288270-chapter-18"
        }
      ],
      "meta": {
        "total": 39, "perPage": 20, "page": 1, "lastPage": 2,
        "from": 1, "to": 20, "hasNext": true, "hasPrev": false
      }
    }
    """;

    /// <summary>Captured <c>GET /api/v1/chapters/11312240</c>, trimmed to 3 pages.</summary>
    private const string ManifestPayload = """
    {
      "id": 11312240,
      "mangaId": 119397,
      "number": 20,
      "groupId": 1387,
      "group": { "id": 1387, "name": "DivaScans" },
      "url": "/title/50xj7-lucky-bitch/11312240-chapter-20",
      "pages": {
        "baseUrl": "",
        "items": [
          { "width": 800, "height": 1334, "url": "https://j24n.wowpic2.store/i5/aaa" },
          { "width": 800, "height": 1334, "url": "https://j24n.wowpic2.store/i5/bbb" },
          { "width": 800, "height": 1332, "url": "https://j24n.wowpic2.store/i5/ccc?8" }
        ]
      }
    }
    """;

    private static IReadOnlyList<JsonObject> ChapterItems() =>
        [.. JsonNode.Parse(ChaptersPayload)!["items"]!.AsArray().OfType<JsonObject>()];

    [Fact]
    public void TheCapturedDetailDecodesAndKeepsTheCallersIdentity()
    {
        var detail = ComixSource.ReadTitleDetail(
            JsonNode.Parse(DetailPayload)!.AsObject(), Title);

        // Identity must not drift between the browse result and the detail, or a
        // confirmed folder mapping and a queued job would name different titles.
        Assert.Equal(Title, detail.Summary.Identity);
        Assert.Equal("Lucky Bitch", detail.Summary.DisplayName);
        Assert.Equal("https://static.comix.ws/9e8f/i/a/97/6a57b776ce3ac@280.jpg", detail.Summary.CoverUrl);
        Assert.Equal("Ch. 20", detail.Summary.LatestChapterLabel);
        Assert.StartsWith("Jeong-im", detail.Description!, StringComparison.Ordinal);

        Assert.Equal(2, detail.Genres.Count);
        Assert.Equal("11", detail.Genres[0].Key);
        Assert.Equal("Drama", detail.Genres[0].DisplayName);

        // The detail header renders "DisplayName: Key", so the label carries the
        // field name and the key carries the captured value.
        Assert.Contains(detail.Metadata, entry => entry.DisplayName == "Type" && entry.Key == "manhwa");
        Assert.Contains(detail.Metadata, entry => entry.DisplayName == "Status" && entry.Key == "releasing");
        Assert.Contains(detail.Metadata, entry => entry.DisplayName == "Authors" && entry.Key == "MAJOR, Yaksu");
    }

    [Fact]
    public void GroupsStayDistinctAndChaptersNeverMixBetweenThem()
    {
        var chapters = ChapterItems();

        var groups = ComixSource.ReadGroups(chapters);
        Assert.Equal(2, groups.Count);
        Assert.Equal("1387", groups[0].Identity.GroupId);
        Assert.Equal("DivaScans", groups[0].DisplayName);
        Assert.Equal("9303", groups[1].Identity.GroupId);
        Assert.Equal("Luna Toons", groups[1].DisplayName);

        var diva = ComixSource.ReadGroupChapters(chapters, Title, groups[0].Identity);
        Assert.Equal(new[] { "20", "19" }, diva.Select(chapter => chapter.Identity.ChapterNumber).ToArray());
        Assert.Equal(new[] { "11312240", "11302252" }, diva.Select(chapter => chapter.Identity.ChapterId).ToArray());
        Assert.All(diva, chapter => Assert.Equal("1387", chapter.Identity.Group.GroupId));

        var luna = ComixSource.ReadGroupChapters(chapters, Title, groups[1].Identity);
        Assert.Equal(new[] { "19", "18" }, luna.Select(chapter => chapter.Identity.ChapterNumber).ToArray());
        Assert.All(luna, chapter => Assert.Equal("9303", chapter.Identity.Group.GroupId));

        // Chapter 19 exists in both groups and must stay two distinct chapters.
        Assert.NotEqual(
            diva.Single(chapter => chapter.Identity.ChapterNumber == "19").Identity.ChapterId,
            luna.Single(chapter => chapter.Identity.ChapterNumber == "19").Identity.ChapterId);
    }

    [Fact]
    public void AnEmptyChapterNameFallsBackToItsNumber()
    {
        var chapters = ChapterItems();
        var diva = ComixSource.ReadGroupChapters(chapters, Title, new RemoteGroupIdentity("comix", "1387"));

        Assert.Equal("Chapter 20", diva[0].DisplayName);
        Assert.Equal(0, diva[0].OrderIndex);
    }

    [Fact]
    public void ADecimalChapterNumberSurvivesAsText()
    {
        var payload = JsonNode.Parse("""
        {
          "items": [
            { "id": 5550001, "number": 10.5, "name": "", "groupId": 1387,
              "group": { "id": 1387, "name": "DivaScans" } }
          ],
          "meta": { "total": 1, "hasNext": false }
        }
        """)!["items"]!.AsArray().OfType<JsonObject>().ToList();

        var chapters = ComixSource.ReadGroupChapters(
            payload, Title, new RemoteGroupIdentity("comix", "1387"));

        Assert.Equal("10.5", chapters[0].Identity.ChapterNumber);
        Assert.Equal("Chapter 10.5", chapters[0].DisplayName);
    }

    [Fact]
    public void TheCapturedManifestKeepsPageOrderAndSuppliesTheReferer()
    {
        var chapter = new RemoteChapterIdentity(
            "comix", Title, "11312240", "20", new RemoteGroupIdentity("comix", "1387"));

        var manifest = ComixSource.ReadManifest(
            JsonNode.Parse(ManifestPayload)!.AsObject(), chapter);

        Assert.Equal(3, manifest.PageCount);
        Assert.Equal(new[] { 0, 1, 2 }, manifest.Pages.Select(page => page.Ordinal).ToArray());
        Assert.Equal(
            new[]
            {
                "https://j24n.wowpic2.store/i5/aaa",
                "https://j24n.wowpic2.store/i5/bbb",
                "https://j24n.wowpic2.store/i5/ccc?8",
            },
            manifest.Pages.Select(page => page.Url).ToArray());
        Assert.Equal(chapter, manifest.Chapter);
        Assert.Equal("https://comix.ws/", manifest.RequestHeaders["Referer"]);
        Assert.StartsWith("sha256:", manifest.ManifestHash, StringComparison.Ordinal);

        // No scramble descriptor was captured, so pages carry no transform and
        // the fetched bytes are kept exactly as they arrive.
        Assert.All(manifest.Pages, page => Assert.Null(page.Transform));
    }

    [Fact]
    public void ARelativePageUrlIsAContractFailureNotAGuessedJoin()
    {
        var chapter = new RemoteChapterIdentity(
            "comix", Title, "11312240", "20", new RemoteGroupIdentity("comix", "1387"));
        var payload = JsonNode.Parse("""
        {
          "pages": { "baseUrl": "", "items": [ { "width": 800, "height": 1200, "url": "/i5/relative" } ] }
        }
        """)!.AsObject();

        Assert.Throws<ComixContractException>(() => ComixSource.ReadManifest(payload, chapter));
    }

    [Fact]
    public void AnEmptyManifestIsRefused()
    {
        var chapter = new RemoteChapterIdentity(
            "comix", Title, "11312240", "20", new RemoteGroupIdentity("comix", "1387"));
        var payload = JsonNode.Parse("""{ "pages": { "baseUrl": "", "items": [] } }""")!.AsObject();

        Assert.Throws<ComixContractException>(() => ComixSource.ReadManifest(payload, chapter));
    }
}
