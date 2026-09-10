using System.Globalization;
using System.IO;
using System.Text.Json;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.DrakeScans;

internal static class DrakeScansRscParser
{
    public static RemoteTitleDetail ParseDetail(string payload, RemoteTitleIdentity identity)
    {
        var series = FindValue(payload, "series", JsonValueKind.Object);
        var title = RequiredString(series, "title");
        var genres = ReadGenres(series);
        var metadata = new List<RemoteOption>();
        AddMetadata(metadata, "Type", String(series, "type"));
        AddMetadata(metadata, "Status", String(series, "status"));
        if (series.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Number)
        {
            AddMetadata(metadata, "Rating", rating.GetDouble().ToString("0.##", CultureInfo.InvariantCulture));
        }

        return new RemoteTitleDetail(
            new RemoteTitleSummary(
                identity,
                title,
                AbsoluteAssetUrl(String(series, "coverImage"), "cover"),
                LatestChapterLabel(payload)),
            String(series, "description"),
            genres,
            metadata);
    }

    public static IReadOnlyList<RemoteChapterSummary> ParseChapters(
        string payload,
        RemoteTitleIdentity title,
        RemoteGroupIdentity group)
        => ParseChapterPage(payload, title, group).Chapters;

    public static DrakeScansChapterPage ParseChapterPage(
        string payload,
        RemoteTitleIdentity title,
        RemoteGroupIdentity group)
    {
        var array = FindValue(payload, "chapters", JsonValueKind.Array);
        var available = new List<(decimal Number, string DisplayName)>();
        var seen = new HashSet<decimal>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new DrakeScansContractException("Drake Scans chapters contains a non-object item.");
            }
            var number = RequiredNumber(item);
            if (!seen.Add(number))
            {
                throw new DrakeScansContractException($"Drake Scans returned duplicate chapter {number}.");
            }
            if (Boolean(item, "isLocked") && !Boolean(item, "hasAccess")) continue;
            available.Add((number, ChapterDisplay(item, number)));
        }

        var chapters = available.OrderBy(item => item.Number)
            .Select((item, index) =>
            {
                var number = item.Number.ToString(CultureInfo.InvariantCulture);
                return new RemoteChapterSummary(
                    new RemoteChapterIdentity(
                        DrakeScansContract.SourceId,
                        title,
                        number,
                        number,
                        group),
                    item.DisplayName,
                    index);
            })
            .ToArray();
        var currentPage = RequiredPositiveInteger(payload, "currentPage");
        var totalPages = RequiredPositiveInteger(payload, "totalPages");
        if (currentPage > totalPages || totalPages > DrakeScansContract.MaximumChapterPages)
        {
            throw new DrakeScansContractException("Drake Scans returned invalid chapter pagination.");
        }
        return new DrakeScansChapterPage(chapters, currentPage, totalPages);
    }

    public static IReadOnlyList<DrakeScansPage> ParsePages(string payload, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var chapter = FindObjectWithArray(payload, "chapter", "pages");
        var result = new List<DrakeScansPage>();
        var seen = new HashSet<int>();
        foreach (var item in chapter.GetProperty("pages").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new DrakeScansContractException("Drake Scans pages contains a non-object item.");
            }
            if (!item.TryGetProperty("pageNumber", out var numberValue)
                || !numberValue.TryGetInt32(out var pageNumber)
                || pageNumber < 1
                || !seen.Add(pageNumber))
            {
                throw new DrakeScansContractException("Drake Scans returned an invalid page number.");
            }
            var rawUrl = RequiredString(item, "imageUrl");
            var url = FullPageUrl(rawUrl, slug);
            result.Add(new DrakeScansPage(String(item, "id") ?? url, pageNumber, url));
        }
        if (result.Count == 0)
        {
            throw new DrakeScansContractException("Drake Scans chapter contains no full pages.");
        }
        return result.OrderBy(page => page.PageNumber).ToArray();
    }

    private static JsonElement FindObjectWithArray(string payload, string property, string childArray)
    {
        var offset = 0;
        while (TryFindValue(payload, property, JsonValueKind.Object, ref offset, out var value))
        {
            if (value.TryGetProperty(childArray, out var child) && child.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }
        throw new DrakeScansContractException(
            $"Drake Scans RSC payload has no '{property}.{childArray}' array.");
    }

    private static JsonElement FindValue(string payload, string property, JsonValueKind kind)
    {
        var offset = 0;
        if (TryFindValue(payload, property, kind, ref offset, out var value)) return value;
        throw new DrakeScansContractException(
            $"Drake Scans RSC payload has no valid '{property}' value.");
    }

    private static int RequiredPositiveInteger(string payload, string property)
    {
        var marker = "\"" + property + "\":";
        var index = payload.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new DrakeScansContractException($"Drake Scans RSC payload has no '{property}'.");
        }
        index += marker.Length;
        while (index < payload.Length && char.IsWhiteSpace(payload[index])) index++;
        var end = index;
        while (end < payload.Length && char.IsAsciiDigit(payload[end])) end++;
        if (end == index
            || !int.TryParse(payload[index..end], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 1)
        {
            throw new DrakeScansContractException($"Drake Scans returned invalid '{property}'.");
        }
        return value;
    }

    private static bool TryFindValue(
        string payload,
        string property,
        JsonValueKind kind,
        ref int offset,
        out JsonElement value)
    {
        var marker = "\"" + property + "\":";
        while (offset < payload.Length)
        {
            var markerIndex = payload.IndexOf(marker, offset, StringComparison.Ordinal);
            if (markerIndex < 0) break;
            var start = markerIndex + marker.Length;
            while (start < payload.Length && char.IsWhiteSpace(payload[start])) start++;
            offset = start + 1;
            var expected = kind == JsonValueKind.Array ? '[' : '{';
            if (start >= payload.Length || payload[start] != expected) continue;
            if (!TryExtractBalanced(payload, start, out var json, out var end)) continue;
            offset = end;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != kind) continue;
                value = document.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                // A streamed RSC document may contain the same property name in
                // unrelated data. Continue until one complete JSON value parses.
            }
        }
        value = default;
        return false;
    }

    private static bool TryExtractBalanced(string text, int start, out string json, out int end)
    {
        var open = text[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"')
            {
                inString = true;
                continue;
            }
            if (current == open) depth++;
            else if (current == close && --depth == 0)
            {
                end = index + 1;
                json = text[start..end];
                return true;
            }
        }
        json = string.Empty;
        end = text.Length;
        return false;
    }

    private static IReadOnlyList<RemoteOption> ReadGenres(JsonElement series)
    {
        if (!series.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<RemoteOption>();
        foreach (var item in genres.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new DrakeScansContractException("Drake Scans detail genres contains a non-object item.");
            }
            result.Add(new RemoteOption(RequiredString(item, "slug"), RequiredString(item, "name")));
        }
        return result;
    }

    private static string? LatestChapterLabel(string payload)
    {
        try
        {
            var chapters = FindValue(payload, "chapters", JsonValueKind.Array);
            decimal? latest = null;
            foreach (var item in chapters.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var number = RequiredNumber(item);
                if (latest is null || number > latest) latest = number;
            }
            return latest is null ? null : "Chapter " + latest.Value.ToString(CultureInfo.InvariantCulture);
        }
        catch (DrakeScansContractException)
        {
            return null;
        }
    }

    private static decimal RequiredNumber(JsonElement item)
    {
        if (!item.TryGetProperty("number", out var value))
        {
            throw new DrakeScansContractException("Drake Scans chapter has no number.");
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numeric)) return numeric;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out numeric))
        {
            return numeric;
        }
        throw new DrakeScansContractException("Drake Scans chapter number is invalid.");
    }

    private static string ChapterDisplay(JsonElement item, decimal number)
    {
        var title = String(item, "title");
        var numeric = number.ToString(CultureInfo.InvariantCulture);
        return title is null || string.Equals(title, numeric, StringComparison.OrdinalIgnoreCase)
            ? "Chapter " + numeric
            : title;
    }

    private static string FullPageUrl(string value, string slug)
    {
        Uri uri;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!string.Equals(absolute.Host, DrakeScansContract.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new DrakeScansContractException("Drake Scans returned a page on an unexpected host.");
            }
            uri = absolute;
        }
        else if (value.StartsWith("/", StringComparison.Ordinal))
        {
            uri = new Uri(DrakeScansContract.BaseUrl + value);
        }
        else
        {
            throw new DrakeScansContractException("Drake Scans returned a malformed page path.");
        }

        var prefix = "/uploads/series/" + slug + "/";
        var file = Path.GetFileName(uri.AbsolutePath);
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            || !file.StartsWith("p-", StringComparison.Ordinal)
            || !file.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            throw new DrakeScansContractException("Drake Scans returned a non-full-page image URL.");
        }
        return uri.ToString();
    }

    private static string? AbsoluteAssetUrl(string? value, string label)
    {
        if (value is null) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!string.Equals(absolute.Host, DrakeScansContract.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new DrakeScansContractException($"Drake Scans returned a {label} on an unexpected host.");
            }
            return absolute.ToString();
        }
        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            throw new DrakeScansContractException($"Drake Scans returned a malformed {label} path.");
        }
        return DrakeScansContract.BaseUrl + value;
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

    private static void AddMetadata(List<RemoteOption> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) metadata.Add(new RemoteOption(key, value));
    }
}
