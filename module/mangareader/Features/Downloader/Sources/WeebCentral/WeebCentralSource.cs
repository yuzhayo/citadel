using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Sources.WeebCentral;

/// <summary>
/// Adapter for Weeb Central's server-rendered HTMX endpoints. Browse and search
/// are one GET to /search/data; the reader fragment carries the whole chapter, so
/// a manifest is a single request. Native HTTP only: no browser session is
/// involved, so this source never needs <see cref="IQueueSourceReadiness"/>.
/// </summary>
public sealed class WeebCentralSource : IMangaSource
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly ProxyHttpTransport? _transport;

    public WeebCentralSource() : this(SharedClient, null)
    {
    }

    internal WeebCentralSource(HttpClient client) : this(client, null)
    {
    }

    internal WeebCentralSource(ProxyHttpTransport transport) : this(SharedClient, transport)
    {
    }

    private WeebCentralSource(HttpClient client, ProxyHttpTransport? transport)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transport = transport;
    }

    public static RemoteSourceGroup Group { get; } = new(
        new RemoteGroupIdentity(WeebCentralContract.SourceId, WeebCentralContract.GroupId),
        WeebCentralContract.DisplayName);

    public string Id => WeebCentralContract.SourceId;

    public string DisplayName => WeebCentralContract.DisplayName;

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
            null => WeebCentralBrowseQuery.Default,
            WeebCentralBrowseQuery weeb => weeb,
            _ => throw new WeebCentralContractException(
                "WeebCentral received a filter owned by another provider."),
        };

        var parameters = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["display_mode"] = [WeebCentralContract.FullDisplay],
            ["limit"] = [WeebCentralContract.PageSize.ToString(CultureInfo.InvariantCulture)],
            ["offset"] = [((request.Page - 1) * WeebCentralContract.PageSize).ToString(CultureInfo.InvariantCulture)],
            ["order"] = [filter.Order],
            // "Any" keeps adult content in the normal catalog; the filter only narrows on request.
            ["official"] = [filter.Official],
            ["anime"] = [filter.Anime],
            ["adult"] = [filter.Adult],
        };

        var keyword = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        // Relevance is the site's default only for a real query; a plain browse
        // sends the panel order (default Popularity/Descending), as CucumberManga does.
        parameters["sort"] = [keyword is null ? filter.SortKey : WeebCentralOptionKeys.Relevance];
        if (keyword is not null)
        {
            parameters["text"] = [keyword];
        }

        AppendMultiValue(parameters, "included_status", filter.Statuses);
        AppendMultiValue(parameters, "included_type", filter.Types);
        AppendMultiValue(parameters, "included_tag", filter.Tags);
        using var message = NewRequest("/search/data?" + QueryString(parameters), "/search");
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return WeebCentralHtmlParser.ParseGrid(html, request.Page);
    }

    public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken) =>
        throw new WeebCentralContractException(
            $"WeebCentral does not expose the '{kind}' lookup in this provider version.");

    public async Task<RemoteTitleDetail> GetTitleAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        ValidateTitle(title);
        using var message = NewRequest(SeriesPath(title), "/");
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return WeebCentralHtmlParser.ParseTitleDetail(html, title);
    }

    /// <summary>Resolves a pasted /series/{id}/{slug} URL into the provider identity.</summary>
    internal async Task<RemoteTitleDetail> ResolveCanonicalSeriesAsync(
        string titleId,
        string slug,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var identity = new RemoteTitleIdentity(
            WeebCentralContract.SourceId, titleId.Trim(), titleId.Trim(), slug.Trim());
        using var message = NewRequest(
            "/series/" + Uri.EscapeDataString(identity.TitleId) + "/" + Uri.EscapeDataString(identity.Slug),
            "/");
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return WeebCentralHtmlParser.ParseTitleDetail(html, identity);
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
        // The detail document contains only a preview. Its "Show All Chapters"
        // control targets this title-id endpoint (without the display slug).
        using var message = NewRequest(
            "/series/" + Uri.EscapeDataString(title.TitleId) + "/full-chapter-list",
            SeriesPath(title));
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        return WeebCentralHtmlParser.ParseChapters(html, title, group);
    }

    public async Task<RemoteChapterManifest> GetManifestAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken)
    {
        ValidateChapter(chapter);
        var chapterPath = ChapterPath(chapter);
        using var message = NewRequest(
            chapterPath + "/images?is_prev=False&current_page=1&reading_style=long_strip",
            chapterPath);
        var html = await SendHtmlAsync(message, cancellationToken).ConfigureAwait(false);
        var urls = WeebCentralHtmlParser.ParsePages(html);
        if (urls.Count == 0)
        {
            throw new WeebCentralContractException(
                "WeebCentral reader returned no pages; the chapter fragment changed.");
        }

        if (urls.Count > WeebCentralContract.MaximumChapterPages)
        {
            throw new WeebCentralContractException(
                $"WeebCentral chapter exceeds the {WeebCentralContract.MaximumChapterPages}-page bound.");
        }

        var pages = urls.Select((url, ordinal) => new RemotePage(ordinal, url, url, null, null)).ToArray();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = WeebCentralContract.BaseUrl + chapterPath,
        };
        return new RemoteChapterManifest(chapter, pages, ManifestHash(chapter, pages), headers);
    }

    public Task<RemotePageImage> TransformPageAsync(
        RemotePage page,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        // The poster CDN declares image/png but serves JPEG bytes under .png, so the
        // format is always taken from the payload, never from the URL or headers.
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

    private async Task<string> SendHtmlAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = _transport is null
            ? await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
            : await _transport.SendAsync("weebcentral", _client, request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > WeebCentralContract.MaximumResponseBytes)
        {
            throw new WeebCentralContractException("WeebCentral response exceeds the 8 MiB bound.");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(payload) > WeebCentralContract.MaximumResponseBytes)
        {
            throw new WeebCentralContractException("WeebCentral response exceeds the 8 MiB bound.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new WeebCentralContractException(
                $"WeebCentral request failed with HTTP {(int)response.StatusCode}.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new WeebCentralContractException(
                $"WeebCentral returned '{mediaType}' instead of HTML.");
        }

        return payload;
    }

    private static HttpRequestMessage NewRequest(string path, string refererPath)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, WeebCentralContract.BaseUrl + path);
        message.Headers.Referrer = new Uri(WeebCentralContract.BaseUrl + refererPath);
        message.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        // The results grid and the reader fragment are HTMX targets.
        message.Headers.TryAddWithoutValidation("HX-Request", "true");
        return message;
    }

    private static void AppendMultiValue(
        Dictionary<string, List<string>> target,
        string key,
        IReadOnlyList<string> values)
    {
        // The Advanced Search form submits one repeated parameter per checked box.
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
        {
            target[key] = [];
        }
        else
        {
            target[key] = [.. distinct];
        }
    }

    private static string QueryString(Dictionary<string, List<string>> values) =>
        string.Join('&', values
            .SelectMany(pair => pair.Value.Select(value =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(value))));

    private static string SeriesPath(RemoteTitleIdentity title)
    {
        var slug = string.IsNullOrWhiteSpace(title.Slug) ? title.TitleHid : title.Slug;
        return "/series/" + Uri.EscapeDataString(title.TitleId) + "/" + Uri.EscapeDataString(slug);
    }

    private static string ChapterPath(RemoteChapterIdentity chapter) =>
        "/chapters/" + Uri.EscapeDataString(chapter.ChapterId);

    private static void ValidateTitle(RemoteTitleIdentity title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!string.Equals(title.SourceId, WeebCentralContract.SourceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(title.TitleId)
            || string.IsNullOrWhiteSpace(title.TitleHid))
        {
            throw new WeebCentralContractException("Invalid WeebCentral title identity.");
        }
    }

    private static void ValidateGroup(RemoteGroupIdentity group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Equals(group, Group.Identity))
        {
            throw new WeebCentralContractException("Invalid WeebCentral source group.");
        }
    }

    private static void ValidateChapter(RemoteChapterIdentity chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ValidateTitle(chapter.Title);
        ValidateGroup(chapter.Group);
        if (!string.Equals(chapter.SourceId, WeebCentralContract.SourceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(chapter.ChapterId))
        {
            throw new WeebCentralContractException("Invalid WeebCentral chapter identity.");
        }
    }

    private static string ManifestHash(
        RemoteChapterIdentity chapter,
        IReadOnlyList<RemotePage> pages)
    {
        var input = new StringBuilder()
            .Append(WeebCentralContract.Version).Append('|')
            .Append(chapter.SourceId).Append('|')
            .Append(chapter.Title.TitleId).Append('|')
            .Append(chapter.ChapterId).Append('|')
            .Append(chapter.Group.GroupId);
        foreach (var page in pages) input.Append('|').Append(page.Ordinal).Append('=').Append(page.Url);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
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

        throw new WeebCentralContractException(
            "WeebCentral page payload is not a recognized image.");
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
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }
}



