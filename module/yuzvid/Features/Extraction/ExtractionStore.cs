using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Module.Yuzvid.Features.Extraction;

/// <summary>
/// Persistence for extraction results. One JSON file per extracted page under
/// <c>%LocalAppData%\Citadel\Yuzvid\extractions\&lt;slug&gt;.json</c>, so results
/// survive an app restart and the Queue can resolve a stored link instead of
/// being handed the live object.
///
/// Mirrors urllib_lib.py's per-page JSON output (snake_case fields, by_server
/// summary) — same mental model, inspectable by hand or by tooling.
///
/// Internal to the Extraction feature (not part of the public contract). Every
/// method is fault-tolerant: on any IO/serialization error it returns null/false
/// and never throws, so a broken cache can never crash browsing or extraction.
/// </summary>
internal static class ExtractionStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "Yuzvid", "extractions");

    /// <summary>
    /// Persists a result to a slug-named file and returns the store key
    /// (the file path) for later resolution. Returns null on failure.
    /// </summary>
    public static string? Save(ExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathFor(SlugOf(result.PageUrl));
            var document = ToDocument(result);
            var json = JsonSerializer.Serialize(document, WriteOptions);
            File.WriteAllText(path, json);
            return path;
        }
        catch
        {
            // Best effort — persistence must never break the extraction pipeline.
            return null;
        }
    }

    /// <summary>
    /// Loads links previously saved under <paramref name="storeKey"/>.
    /// Returns null when the key is unknown, missing, or corrupt.
    /// </summary>
    public static IReadOnlyList<VideoLink>? Resolve(string? storeKey)
    {
        if (string.IsNullOrEmpty(storeKey)) return null;
        try
        {
            if (!File.Exists(storeKey)) return null;
            var json = File.ReadAllText(storeKey);
            var document = JsonSerializer.Deserialize<StoredExtraction>(json, ReadOptions);
            if (document?.Links is null) return null;

            var links = new List<VideoLink>(document.Links.Count);
            foreach (var stored in document.Links)
            {
                // Label is re-supplied so igay69 titles survive the round-trip;
                // version group derives from host either way.
                var link = VideoLinkExtractor.TryBuildFromUri(stored.Full, stored.Label);
                if (link is not null) links.Add(link);
            }
            return links;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds one stored link by identity. <paramref name="linkKey"/> is matched
    /// against the link's <see cref="VideoLink.DedupKey"/>; falls back to slug
    /// (last path segment) when the caller only has that.
    /// </summary>
    public static VideoLink? ResolveLink(string? storeKey, string? linkKey)
    {
        var links = Resolve(storeKey);
        if (links is null || string.IsNullOrEmpty(linkKey)) return null;

        foreach (var link in links)
        {
            if (string.Equals(link.DedupKey, linkKey, StringComparison.OrdinalIgnoreCase))
                return link;
        }
        foreach (var link in links)
        {
            if (string.Equals(link.Slug, linkKey, StringComparison.OrdinalIgnoreCase))
                return link;
        }
        return null;
    }

    private static string SlugOf(string pageUrl)
    {
        var slug = VideoLinkExtractor.TryBuildFromUri(pageUrl)?.Slug;
        if (!string.IsNullOrEmpty(slug)) return Sanitize(slug);

        // Fallback: last non-empty path segment, else a stable generic name.
        try
        {
            var path = new Uri(pageUrl, UriKind.Absolute).AbsolutePath.TrimEnd('/');
            var segment = path[(path.LastIndexOf('/') + 1)..];
            if (!string.IsNullOrEmpty(segment)) return Sanitize(segment);
        }
        catch { }
        return "page";
    }

    private static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name.Length > 80 ? name[..80] : name;
    }

    private static string PathFor(string slug) => Path.Combine(Folder, slug + ".json");

    // ─── JSON document (snake_case, parity with urllib_lib.py) ──────────────

    private static StoredExtraction ToDocument(ExtractionResult result)
    {
        var byServer = new Dictionary<string, int>(StringComparer.Ordinal);
        var links = new List<StoredLink>(result.Links.Count);

        foreach (var link in result.Links)
        {
            byServer.TryGetValue(link.ServerGroup, out var count);
            byServer[link.ServerGroup] = count + 1;

            links.Add(new StoredLink
            {
                Full = link.Full,
                Host = link.Host,
                Path = link.Path,
                Slug = link.Slug,
                EpisodeNum = link.EpisodeNum,
                EpisodeTitle = link.EpisodeTitle,
                ServerGroup = link.ServerGroup,
                IsDirectMedia = link.IsDirectMedia,
                VersionGroup = link.VersionGroup,
                Label = link.Label,
                Title = link.Title,
            });
        }

        return new StoredExtraction
        {
            PageUrl = result.PageUrl,
            Slug = SlugOf(result.PageUrl),
            ExtractedAt = DateTime.UtcNow.ToString("o"),
            Total = links.Count,
            ByServer = byServer,
            Links = links,
        };
    }

    private sealed class StoredExtraction
    {
        [JsonPropertyName("page_url")] public string PageUrl { get; set; } = "";
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("extracted_at")] public string ExtractedAt { get; set; } = "";
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("by_server")] public Dictionary<string, int> ByServer { get; set; } = new();
        [JsonPropertyName("links")] public List<StoredLink> Links { get; set; } = new();
    }

    private sealed class StoredLink
    {
        [JsonPropertyName("full")] public string Full { get; set; } = "";
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("episode_num")] public int? EpisodeNum { get; set; }
        [JsonPropertyName("version_group")] public string VersionGroup { get; set; } = "";
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("episode_title")] public string? EpisodeTitle { get; set; }
        [JsonPropertyName("server_group")] public string ServerGroup { get; set; } = "";
        [JsonPropertyName("is_direct_media")] public bool IsDirectMedia { get; set; }
    }
}
