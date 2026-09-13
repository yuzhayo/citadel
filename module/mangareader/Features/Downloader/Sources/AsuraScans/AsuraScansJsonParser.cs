using System.Text.Json;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.AsuraScans;

internal static class AsuraScansJsonParser
{
    public static RemoteTitleDetail ParseTitle(string payload, RemoteTitleIdentity identity)
    {
        using var document = Parse(payload);
        var root = document.RootElement;
        var series = root.TryGetProperty("series", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : throw new AsuraScansContractException("AsuraScans detail response has no series object.");
        var title = RequiredString(series, "title");
        var genres = series.TryGetProperty("genres", out var rawGenres) && rawGenres.ValueKind == JsonValueKind.Array
            ? rawGenres.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new RemoteOption(String(item, "slug") ?? RequiredString(item, "name"), RequiredString(item, "name"))).ToArray()
            : [];
        var metadata = new List<RemoteOption>();
        if (String(series, "type") is { } type) metadata.Add(new("Type", type));
        if (String(series, "status") is { } status) metadata.Add(new("Status", status));
        if (String(series, "author") is { } author) metadata.Add(new("Author", author));
        if (String(series, "artist") is { } artist) metadata.Add(new("Artist", artist));
        return new RemoteTitleDetail(
            new RemoteTitleSummary(identity, title, Cover(series), LatestChapter(series))
            {
                CoverRequestHeaders = RefererHeaders(),
            },
            String(series, "description"), genres, metadata);
    }

    public static RemoteCatalogPage ParseCatalog(string payload, int requestedPage)
    {
        using var document = Parse(payload);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array
            ? wrapped
            : root.ValueKind == JsonValueKind.Array
                ? root
                : throw new AsuraScansContractException("AsuraScans catalog response has no data array.");

        var items = new List<RemoteTitleSummary>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var slug = RequiredString(item, "slug");
            var title = RequiredString(item, "title");
            var id = String(item, "id") ?? slug;
            items.Add(Summary(slug, id, title, Cover(item), LatestChapter(item)));
        }

        var meta = root.TryGetProperty("meta", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? metadata
            : root;
        var total = Number(meta, "total") ?? Number(root, "total") ?? Number(root, "count");
        var hasMore = Boolean(meta, "has_more") || Boolean(meta, "hasMore");
        return new RemoteCatalogPage(items, total, requestedPage, hasMore);
    }

    private static RemoteTitleSummary Summary(string slug, string id, string title, string? cover, string? latest) =>
        new(new RemoteTitleIdentity(AsuraScansContract.SourceId, slug, id, slug), title, cover, latest)
        {
            CoverRequestHeaders = RefererHeaders(),
        };

    internal static IReadOnlyDictionary<string, string> RefererHeaders() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = AsuraScansContract.BaseUrl + "/",
        };

    private static string? Cover(JsonElement item) =>
        String(item, "cover_url") ?? String(item, "coverUrl") ?? String(item, "cover");

    private static string? LatestChapter(JsonElement item)
    {
        var value = String(item, "latest_chapter")
            ?? String(item, "latestChapter")
            ?? String(item, "chapter_count");
        return value is null ? null : "Chapter " + value;
    }

    private static JsonDocument Parse(string payload)
    {
        try { return JsonDocument.Parse(payload); }
        catch (JsonException exception)
        {
            throw new AsuraScansContractException("AsuraScans response is invalid JSON: " + exception.Message);
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        String(element, name) ?? throw new AsuraScansContractException($"AsuraScans item has no '{name}'.");

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString().Trim() is { Length: > 0 } text ? text : null
            : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static bool Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

}
