using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.CucumberManga;

public static class CucumberMangaContract
{
    public const int Version = 1;
    public const string SourceId = "cucumber-manga";
    public const string DisplayName = "Cucumber Manga";
    public const string BaseUrl = "https://cucumbermanga.com";
    public const string Host = "cucumbermanga.com";
    public const int PageSize = 25;
    public const string GroupId = "cucumbermanga";
}

public sealed class CucumberMangaContractException(string message) : InvalidOperationException(message);

public enum CucumberMangaAdultMode
{
    All,
    Exclude,
    Only,
}

public sealed record CucumberMangaSortOption(string Key, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record CucumberMangaAdultOption(CucumberMangaAdultMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record CucumberMangaBrowseQuery : IRemoteBrowseFilter
{
    public static CucumberMangaBrowseQuery Default { get; } = new();

    public string SourceId => CucumberMangaContract.SourceId;

    public string SortKey { get; init; } = "latest";

    public IReadOnlyList<string> Statuses { get; init; } = [];

    public IReadOnlyList<string> Genres { get; init; } = [];

    public CucumberMangaAdultMode AdultMode { get; init; }

    public string? Author { get; init; }

    public string? Artist { get; init; }

    public string? ReleaseYear { get; init; }
}

/// <summary>Values captured from Cucumber Manga's live archive filters.</summary>
public static class CucumberMangaOptions
{
    public static IReadOnlyList<CucumberMangaSortOption> Sorts { get; } =
    [
        new("relevance", "Relevance"),
        new("latest", "Latest update"),
        new("alphabet", "Title (A-Z)"),
        new("rating", "Highest rated"),
        new("trending", "Trending"),
        new("views", "Most viewed"),
        new("new-manga", "Recently added"),
    ];

    public static IReadOnlyList<RemoteOption> Statuses { get; } =
    [
        new("end", "Completed"),
        new("on-going", "Ongoing"),
        new("canceled", "Canceled"),
        new("on-hold", "On hold"),
    ];

    public static IReadOnlyList<CucumberMangaAdultOption> AdultModes { get; } =
    [
        new(CucumberMangaAdultMode.All, "All content"),
        new(CucumberMangaAdultMode.Exclude, "Exclude adult"),
        new(CucumberMangaAdultMode.Only, "Adult only"),
    ];

    public static IReadOnlyList<RemoteOption> Genres { get; } =
    [
        new("action", "Action"), new("adult", "Adult"), new("adventure", "Adventure"),
        new("anime", "Anime"), new("comedy", "Comedy"), new("comic", "Comic"),
        new("cooking", "Cooking"), new("doujinshi", "Doujinshi"), new("drama", "Drama"),
        new("ecchi", "Ecchi"), new("fantasy", "Fantasy"), new("gender-bender", "Gender Bender"),
        new("harem", "Harem"), new("historical", "Historical"), new("horror", "Horror"),
        new("josei", "Josei"), new("live-action", "Live action"), new("manga", "Manga"),
        new("manhua", "Manhua"), new("manhwa", "Manhwa"), new("martial-arts", "Martial Arts"),
        new("mature", "Mature"), new("mecha", "Mecha"), new("mystery", "Mystery"),
        new("one-shot", "One shot"), new("psychological", "Psychological"),
        new("romance", "Romance"), new("school-life", "School Life"), new("sci-fi", "Sci-fi"),
        new("seinen", "Seinen"), new("shoujo", "Shoujo"), new("shoujo-ai", "Shoujo Ai"),
        new("shounen", "Shounen"), new("shounen-ai", "Shounen Ai"),
        new("slice-of-life", "Slice of Life"), new("smut", "Smut"),
        new("soft-yaoi", "Soft Yaoi"), new("soft-yuri", "Soft Yuri"),
        new("sports", "Sports"), new("supernatural", "Supernatural"),
        new("tragedy", "Tragedy"), new("yaoi", "Yaoi"), new("yuri", "Yuri"),
    ];
}

/// <summary>
/// Native HTTP adapter for Cucumber Manga's observed WordPress routes. It does
/// not share Comix signing, cipher, browser bootstrap, filters, or session state.
/// </summary>
public sealed class CucumberMangaSource : IMangaSource
{
    private const int MaximumHtmlBytes = 8 * 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;

    public CucumberMangaSource() : this(SharedClient)
    {
    }

    internal CucumberMangaSource(HttpClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public static RemoteSourceGroup Group { get; } = new(
        new RemoteGroupIdentity(CucumberMangaContract.SourceId, CucumberMangaContract.GroupId),
        CucumberMangaContract.DisplayName);

    public string Id => CucumberMangaContract.SourceId;

    public string DisplayName => CucumberMangaContract.DisplayName;

    public MangaSourceCapabilities Capabilities { get; } = new(
        SupportsSearch: true,
        SupportsAdvancedFilters: true,
        LookupKinds: [],
        TransformsPages: false);

    public async Task<RemoteCatalogPage> BrowseAsync(
        RemoteBrowseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request.Page));
        var filter = request.Filter switch
        {
            null => CucumberMangaBrowseQuery.Default,
            CucumberMangaBrowseQuery cucumber => cucumber,
            _ => throw new CucumberMangaContractException(
                "Cucumber Manga received a filter owned by another provider."),
        };

        var fields = BaseBrowseFields(request.Page);
        var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        if (query is null)
        {
            AddSort(fields, filter.SortKey);
        }
        else
        {
            // With no explicit order, Madara/WordPress ranks a title query by
            // relevance. Adding latest here would destroy best-match search.
            fields.Add(new("vars[s]", query));
        }

        AddFilters(fields, filter);

        using var message = NewRequest(HttpMethod.Post, "/wp-admin/admin-ajax.php", xhr: true);
        message.Content = new FormUrlEncodedContent(fields);
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return CucumberMangaHtmlParser.ParseBrowse(html, request.Page);
    }

    public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken) =>
        throw new CucumberMangaContractException(
            $"Cucumber Manga does not expose the '{kind}' lookup in this provider version.");

    public async Task<RemoteTitleDetail> GetTitleAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        ValidateTitle(title);
        using var message = NewRequest(HttpMethod.Get, TitlePath(title));
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return CucumberMangaHtmlParser.ParseTitleDetail(html, title);
    }

    internal async Task<RemoteTitleDetail> ResolveCanonicalTitleAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        using var message = NewRequest(
            HttpMethod.Get,
            "/manga/" + Uri.EscapeDataString(slug) + "/");
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return CucumberMangaHtmlParser.ParseTitleDetailFromCanonicalPath(html, slug);
    }

    public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        ValidateTitle(title);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RemoteSourceGroup>>([Group]);
    }

    public async Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        CancellationToken cancellationToken)
    {
        ValidateTitle(title);
        ValidateGroup(group);
        using var message = NewRequest(
            HttpMethod.Post,
            TitlePath(title) + "ajax/chapters/",
            xhr: true);
        message.Content = new FormUrlEncodedContent([]);
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return CucumberMangaHtmlParser.ParseChapters(html, title, group);
    }

    public async Task<RemoteChapterManifest> GetManifestAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken)
    {
        ValidateChapter(chapter);
        var chapterPath = TitlePath(chapter.Title)
            + Uri.EscapeDataString(chapter.ChapterId)
            + "/";
        using var message = NewRequest(HttpMethod.Get, chapterPath);
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        var urls = CucumberMangaHtmlParser.ParsePages(html);

        var pages = urls.Select((url, ordinal) => new RemotePage(
            ordinal,
            RemoteKey: url,
            Url: url,
            ExpectedBytes: null,
            Transform: null)).ToArray();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = CucumberMangaContract.BaseUrl + chapterPath,
        };
        return new RemoteChapterManifest(
            chapter,
            pages,
            ManifestHash(chapter, pages),
            headers);
    }

    public Task<RemotePageImage> TransformPageAsync(
        RemotePage page,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RemotePageImage(payload, DetectFormat(payload)));
    }

    public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken)
    {
        ValidateChapter(chapter);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RemoteAlternateChapter>>([]);
    }

    private static List<KeyValuePair<string, string>> BaseBrowseFields(int page) =>
    [
        new("action", "madara_load_more"),
        new("page", (page - 1).ToString(CultureInfo.InvariantCulture)),
        new("template", "madara-core/content/content-archive"),
        new("vars[paged]", "1"),
        new("vars[template]", "archive"),
        new("vars[posts_per_page]", CucumberMangaContract.PageSize.ToString(CultureInfo.InvariantCulture)),
        new("vars[post_type]", "wp-manga"),
        new("vars[post_status]", "publish"),
        new("vars[manga_archives_item_layout]", "big_thumbnail"),
        new("vars[meta_query][0][key]", "_wp_manga_chapter_type"),
        new("vars[meta_query][0][value]", "manga"),
    ];

    private static void AddSort(List<KeyValuePair<string, string>> fields, string sortKey)
    {
        switch (sortKey)
        {
            case "relevance":
                return;
            case "latest":
                AddMetaSort(fields, "_latest_update");
                return;
            case "alphabet":
                fields.Add(new("vars[orderby]", "post_title"));
                fields.Add(new("vars[order]", "ASC"));
                return;
            case "rating":
                fields.Add(new("vars[meta_query][query_average_reviews][key]", "_manga_avarage_reviews"));
                fields.Add(new("vars[meta_query][query_average_reviews][compare]", "EXISTS"));
                fields.Add(new("vars[meta_query][query_total_reviews][key]", "_manga_total_votes"));
                fields.Add(new("vars[meta_query][query_total_reviews][compare]", "EXISTS"));
                fields.Add(new("vars[orderby][query_average_reviews]", "DESC"));
                fields.Add(new("vars[orderby][query_total_reviews]", "DESC"));
                return;
            case "trending":
                AddMetaSort(fields, "_wp_manga_week_views_value");
                return;
            case "views":
                AddMetaSort(fields, "_wp_manga_views");
                return;
            case "new-manga":
                fields.Add(new("vars[orderby]", "date"));
                fields.Add(new("vars[order]", "DESC"));
                return;
            default:
                throw new CucumberMangaContractException(
                    $"Unknown Cucumber Manga sort '{sortKey}'.");
        }
    }

    private static void AddMetaSort(List<KeyValuePair<string, string>> fields, string metaKey)
    {
        fields.Add(new("vars[orderby]", "meta_value_num"));
        fields.Add(new("vars[meta_key]", metaKey));
        fields.Add(new("vars[order]", "DESC"));
    }

    private static void AddFilters(
        List<KeyValuePair<string, string>> fields,
        CucumberMangaBrowseQuery filter)
    {
        var metaIndex = 1;
        if (filter.Statuses.Count > 0)
        {
            fields.Add(new($"vars[meta_query][{metaIndex}][key]", "_wp_manga_status"));
            fields.Add(new($"vars[meta_query][{metaIndex}][compare]", "IN"));
            for (var index = 0; index < filter.Statuses.Count; index++)
            {
                fields.Add(new($"vars[meta_query][{metaIndex}][value][{index}]", filter.Statuses[index]));
            }
            metaIndex++;
        }

        if (filter.AdultMode != CucumberMangaAdultMode.All)
        {
            fields.Add(new($"vars[meta_query][{metaIndex}][key]", "manga_adult_content"));
            fields.Add(new(
                $"vars[meta_query][{metaIndex}][compare]",
                filter.AdultMode == CucumberMangaAdultMode.Exclude ? "not exists" : "exists"));
        }

        var taxonomyIndex = 0;
        AddTextTaxonomy(fields, ref taxonomyIndex, "wp-manga-author", filter.Author);
        AddTextTaxonomy(fields, ref taxonomyIndex, "wp-manga-artist", filter.Artist);
        AddTextTaxonomy(fields, ref taxonomyIndex, "wp-manga-release", filter.ReleaseYear);

        if (filter.Genres.Count > 0)
        {
            fields.Add(new($"vars[tax_query][{taxonomyIndex}][taxonomy]", "wp-manga-genre"));
            fields.Add(new($"vars[tax_query][{taxonomyIndex}][field]", "slug"));
            for (var index = 0; index < filter.Genres.Count; index++)
            {
                fields.Add(new($"vars[tax_query][{taxonomyIndex}][terms][{index}]", filter.Genres[index]));
            }
        }
    }

    private static void AddTextTaxonomy(
        List<KeyValuePair<string, string>> fields,
        ref int taxonomyIndex,
        string taxonomy,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        fields.Add(new($"vars[tax_query][{taxonomyIndex}][taxonomy]", taxonomy));
        fields.Add(new($"vars[tax_query][{taxonomyIndex}][field]", "name"));
        fields.Add(new($"vars[tax_query][{taxonomyIndex}][terms]", value.Trim()));
        taxonomyIndex++;
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, bool xhr = false)
    {
        var message = new HttpRequestMessage(method, CucumberMangaContract.BaseUrl + path);
        message.Headers.Referrer = new Uri(CucumberMangaContract.BaseUrl + "/");
        message.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        if (xhr)
        {
            message.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        }

        return message;
    }

    private async Task<string> SendHtmlAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaximumHtmlBytes)
        {
            throw new CucumberMangaContractException(
                "Cucumber Manga HTML exceeds the 8 MiB response bound.");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(html) > MaximumHtmlBytes)
        {
            throw new CucumberMangaContractException(
                "Cucumber Manga HTML exceeds the 8 MiB response bound.");
        }

        if (LooksLikeChallenge(html))
        {
            throw new CucumberMangaContractException(
                "Cucumber Manga returned a Cloudflare challenge; no catalog data was accepted.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CucumberMangaContractException(
                $"Cucumber Manga request failed with HTTP {(int)response.StatusCode}.");
        }

        return html;
    }

    private static bool LooksLikeChallenge(string html) =>
        html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Sorry, you have been blocked", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Cloudflare Ray ID", StringComparison.OrdinalIgnoreCase);

    private static string TitlePath(RemoteTitleIdentity title) =>
        "/manga/" + Uri.EscapeDataString(title.TitleHid) + "/";

    private static void ValidateTitle(RemoteTitleIdentity title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!string.Equals(title.SourceId, CucumberMangaContract.SourceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(title.TitleId)
            || string.IsNullOrWhiteSpace(title.TitleHid))
        {
            throw new CucumberMangaContractException("Invalid Cucumber Manga title identity.");
        }
    }

    private static void ValidateGroup(RemoteGroupIdentity group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Equals(group, Group.Identity))
        {
            throw new CucumberMangaContractException("Invalid Cucumber Manga source group.");
        }
    }

    private static void ValidateChapter(RemoteChapterIdentity chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ValidateTitle(chapter.Title);
        ValidateGroup(chapter.Group);
        if (!string.Equals(chapter.SourceId, CucumberMangaContract.SourceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(chapter.ChapterId))
        {
            throw new CucumberMangaContractException("Invalid Cucumber Manga chapter identity.");
        }
    }

    private static string ManifestHash(
        RemoteChapterIdentity chapter,
        IReadOnlyList<RemotePage> pages)
    {
        var builder = new StringBuilder();
        builder.Append(CucumberMangaContract.Version).Append('|')
            .Append(chapter.SourceId).Append('|')
            .Append(chapter.Title.TitleId).Append('|')
            .Append(chapter.ChapterId).Append('|')
            .Append(chapter.Group.GroupId);
        foreach (var page in pages)
        {
            builder.Append('|').Append(page.Ordinal).Append('=').Append(page.Url);
        }

        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string DetectFormat(byte[] payload)
    {
        if (payload.Length >= 4
            && payload[0] == 0x89 && payload[1] == 0x50
            && payload[2] == 0x4E && payload[3] == 0x47)
        {
            return "png";
        }

        if (payload.Length >= 3
            && payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF)
        {
            return "jpg";
        }

        if (payload.Length >= 12
            && payload[0] == 0x52 && payload[1] == 0x49 && payload[2] == 0x46 && payload[3] == 0x46
            && payload[8] == 0x57 && payload[9] == 0x45 && payload[10] == 0x42 && payload[11] == 0x50)
        {
            return "webp";
        }

        if (payload.Length >= 3
            && payload[0] == 0x47 && payload[1] == 0x49 && payload[2] == 0x46)
        {
            return "gif";
        }

        throw new CucumberMangaContractException(
            "Cucumber Manga page payload is not a recognized image.");
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }
}
