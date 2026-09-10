using System.Globalization;
using System.Text.Json;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.DrakeScans;

internal static class DrakeScansJsonParser
{
    public static RemoteCatalogPage ParseCatalog(string json, int requestedPage)
    {
        using var document = ParseDocument(json, "catalog response");
        var root = document.RootElement;
        var data = RequiredProperty(root, "data", JsonValueKind.Array);
        var meta = RequiredProperty(root, "meta", JsonValueKind.Object);
        var items = new List<RemoteTitleSummary>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new DrakeScansContractException("Drake Scans catalog contains a non-object item.");
            }
            if (Boolean(item, "dmcaTakenDown")) continue;

            var id = RequiredString(item, "id");
            var slug = String(item, "urlSlug") ?? RequiredString(item, "slug");
            var title = RequiredString(item, "title");
            items.Add(new RemoteTitleSummary(
                new RemoteTitleIdentity(DrakeScansContract.SourceId, slug, id, slug),
                title,
                AbsoluteUrl(String(item, "coverImage")),
                LatestChapterLabel(item)));
        }

        var total = meta.TryGetProperty("total", out var totalValue)
            && totalValue.TryGetInt64(out var parsedTotal) ? parsedTotal : (long?)null;
        var page = meta.TryGetProperty("page", out var pageValue)
            && pageValue.TryGetInt32(out var parsedPage) ? parsedPage : requestedPage;
        var hasMore = meta.TryGetProperty("hasMore", out var moreValue)
            && moreValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && moreValue.GetBoolean();
        return new RemoteCatalogPage(items, total, page, hasMore);
    }

    public static IReadOnlyList<RemoteLookupOption> ParseGenres(string json)
    {
        using var document = ParseDocument(json, "genre response");
        var genres = RequiredProperty(document.RootElement, "genres", JsonValueKind.Array);
        var result = new List<RemoteLookupOption>();
        foreach (var item in genres.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new DrakeScansContractException("Drake Scans genres contains a non-object item.");
            }
            result.Add(new RemoteLookupOption(RequiredString(item, "slug"), RequiredString(item, "name")));
        }
        return result.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? LatestChapterLabel(JsonElement item)
    {
        if (!item.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var chapter in chapters.EnumerateArray())
        {
            if (chapter.ValueKind != JsonValueKind.Object) continue;
            if (TryNumber(chapter, out var number))
            {
                return "Chapter " + number.ToString(CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    private static bool TryNumber(JsonElement item, out decimal number)
    {
        number = 0;
        if (!item.TryGetProperty("number", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out number)) return true;
        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    private static JsonElement RequiredProperty(JsonElement element, string property, JsonValueKind kind)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != kind)
        {
            throw new DrakeScansContractException(
                $"Drake Scans payload is missing a valid '{property}' value.");
        }
        return value;
    }

    private static string RequiredString(JsonElement element, string property) =>
        String(element, property)
        ?? throw new DrakeScansContractException($"Drake Scans payload is missing '{property}'.");

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static string? AbsoluteUrl(string? value)
    {
        if (value is null) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!string.Equals(absolute.Host, DrakeScansContract.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new DrakeScansContractException("Drake Scans returned a cover on an unexpected host.");
            }
            return absolute.ToString();
        }
        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            throw new DrakeScansContractException("Drake Scans returned a malformed cover path.");
        }
        return DrakeScansContract.BaseUrl + value;
    }

    private static JsonDocument ParseDocument(string json, string label)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new DrakeScansContractException($"Drake Scans {label} is invalid JSON: {ex.Message}");
        }
    }
}
