using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Sources.DrakeScans;

public sealed class DrakeScansSource : IMangaSource
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly ProxyHttpTransport? _transport;

    public DrakeScansSource() : this(SharedClient, null)
    {
    }

    internal DrakeScansSource(HttpClient client) : this(client, null)
    {
    }

    internal DrakeScansSource(ProxyHttpTransport transport) : this(SharedClient, transport)
    {
    }

    private DrakeScansSource(HttpClient client, ProxyHttpTransport? transport)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transport = transport;
    }

    public static RemoteSourceGroup Group { get; } = new(
        new RemoteGroupIdentity(DrakeScansContract.SourceId, DrakeScansContract.GroupId),
        DrakeScansContract.DisplayName);

    public string Id => DrakeScansContract.SourceId;

    public string DisplayName => DrakeScansContract.DisplayName;

    public MangaSourceCapabilities Capabilities { get; } = new(
        SupportsSearch: true,
        SupportsAdvancedFilters: true,
        LookupKinds: [RemoteLookupKind.Genre],
        TransformsPages: false);

    public async Task<RemoteCatalogPage> BrowseAsync(
        RemoteBrowseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request.Page));
        var filter = request.Filter switch
        {
            null => DrakeScansBrowseQuery.Default,
            DrakeScansBrowseQuery drake => drake,
            _ => throw new DrakeScansContractException(
                "Drake Scans received a filter owned by another provider."),
        };

        var values = new List<KeyValuePair<string, string>>
        {
            new("page", request.Page.ToString(CultureInfo.InvariantCulture)),
            new("limit", DrakeScansContract.PageSize.ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(request.Query)) values.Add(new("q", request.Query.Trim()));
        AddList(values, "genre", filter.Genres);
        AddList(values, "status", filter.Statuses);

        using var message = JsonRequest("/api/series?" + QueryString(values));
        var json = await SendAsync(message, "application/json", cancellationToken).ConfigureAwait(false);
        return DrakeScansJsonParser.ParseCatalog(json, request.Page);
    }

    public async Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken)
    {
        if (kind != RemoteLookupKind.Genre)
        {
            throw new DrakeScansContractException($"Drake Scans does not expose the '{kind}' lookup.");
        }
        using var message = JsonRequest("/api/genres");
        var json = await SendAsync(message, "application/json", cancellationToken).ConfigureAwait(false);
        var options = DrakeScansJsonParser.ParseGenres(json);
        var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        return normalized is null
            ? options
            : options.Where(option => option.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    public async Task<RemoteTitleDetail> GetTitleAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        ValidateTitle(title);
        var payload = await GetTitlePayloadAsync(title, cancellationToken).ConfigureAwait(false);
        return DrakeScansRscParser.ParseDetail(payload, title);
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
        var chapters = new List<RemoteChapterSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pageNumber = 1;
        var totalPages = 1;
        do
        {
            var payload = await GetTitlePayloadAsync(title, pageNumber, cancellationToken).ConfigureAwait(false);
            var page = DrakeScansRscParser.ParseChapterPage(payload, title, group);
            if (page.CurrentPage != pageNumber)
            {
                throw new DrakeScansContractException("Drake Scans returned an unexpected chapter page.");
            }
            totalPages = page.TotalPages;
            foreach (var chapter in page.Chapters)
            {
                if (!seen.Add(chapter.Identity.ChapterId))
                {
                    throw new DrakeScansContractException(
                        $"Drake Scans returned duplicate chapter {chapter.Identity.ChapterNumber} across pages.");
                }
                chapters.Add(chapter);
            }
            pageNumber++;
        }
        while (pageNumber <= totalPages);

        return chapters.Select((chapter, index) => chapter with { OrderIndex = index }).ToArray();
    }

    public async Task<RemoteChapterManifest> GetManifestAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken)
    {
        ValidateChapter(chapter);
        var route = ChapterPath(chapter);
        using var message = RscRequest(route + "?_rsc=1");
        var payload = await SendAsync(message, "text/x-component", cancellationToken).ConfigureAwait(false);
        var parsed = DrakeScansRscParser.ParsePages(payload, RouteSlug(chapter.Title));
        var pages = parsed.Select((page, ordinal) => new RemotePage(
            ordinal,
            page.RemoteKey,
            page.Url,
            ExpectedBytes: null,
            Transform: null)).ToArray();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = DrakeScansContract.BaseUrl + route,
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

    private async Task<string> GetTitlePayloadAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
        => await GetTitlePayloadAsync(title, 1, cancellationToken).ConfigureAwait(false);

    private async Task<string> GetTitlePayloadAsync(
        RemoteTitleIdentity title,
        int page,
        CancellationToken cancellationToken)
    {
        using var message = RscRequest(TitlePath(title) + "?sort=asc&page="
            + page.ToString(CultureInfo.InvariantCulture) + "&_rsc=1");
        return await SendAsync(message, "text/x-component", cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(
        HttpRequestMessage request,
        string expectedMediaType,
        CancellationToken cancellationToken)
    {
        using var response = _transport is null
            ? await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
            : await _transport.SendAsync("drake", _client, request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > DrakeScansContract.MaximumResponseBytes)
        {
            throw new DrakeScansContractException("Drake Scans response exceeds the 8 MiB bound.");
        }
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(payload) > DrakeScansContract.MaximumResponseBytes)
        {
            throw new DrakeScansContractException("Drake Scans response exceeds the 8 MiB bound.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new DrakeScansContractException(
                $"Drake Scans request failed with HTTP {(int)response.StatusCode}.");
        }
        var actualMediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(actualMediaType, expectedMediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new DrakeScansContractException(
                $"Drake Scans returned '{actualMediaType ?? "unknown"}' instead of {expectedMediaType}.");
        }
        return payload;
    }

    private static HttpRequestMessage JsonRequest(string path)
    {
        var message = NewRequest(path);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return message;
    }

    private static HttpRequestMessage RscRequest(string path)
    {
        var message = NewRequest(path);
        message.Headers.TryAddWithoutValidation("Accept", "text/x-component");
        message.Headers.TryAddWithoutValidation("RSC", "1");
        return message;
    }

    private static HttpRequestMessage NewRequest(string path)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, DrakeScansContract.BaseUrl + path);
        message.Headers.Referrer = new Uri(DrakeScansContract.BaseUrl + "/");
        return message;
    }

    private static void AddList(
        List<KeyValuePair<string, string>> target,
        string key,
        IReadOnlyList<string> values)
    {
        var normalized = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length > 0) target.Add(new(key, string.Join(',', normalized)));
    }

    private static string QueryString(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join('&', values.Select(value =>
            Uri.EscapeDataString(value.Key) + "=" + Uri.EscapeDataString(value.Value)));

    private static string TitlePath(RemoteTitleIdentity title) =>
        "/series/comic/" + Uri.EscapeDataString(RouteSlug(title));

    private static string RouteSlug(RemoteTitleIdentity title) =>
        string.IsNullOrWhiteSpace(title.Slug) ? title.TitleId : title.Slug;

    private static string ChapterPath(RemoteChapterIdentity chapter) =>
        TitlePath(chapter.Title) + "/chapter/" + Uri.EscapeDataString(chapter.ChapterNumber);

    private static void ValidateTitle(RemoteTitleIdentity title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!string.Equals(title.SourceId, DrakeScansContract.SourceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(title.TitleId)
            || string.IsNullOrWhiteSpace(title.TitleHid))
        {
            throw new DrakeScansContractException("Invalid Drake Scans title identity.");
        }
    }

    private static void ValidateGroup(RemoteGroupIdentity group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Equals(group, Group.Identity))
        {
            throw new DrakeScansContractException("Invalid Drake Scans source group.");
        }
    }

    private static void ValidateChapter(RemoteChapterIdentity chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ValidateTitle(chapter.Title);
        ValidateGroup(chapter.Group);
        if (!string.Equals(chapter.SourceId, DrakeScansContract.SourceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(chapter.ChapterId)
            || string.IsNullOrWhiteSpace(chapter.ChapterNumber))
        {
            throw new DrakeScansContractException("Invalid Drake Scans chapter identity.");
        }
    }

    private static string ManifestHash(
        RemoteChapterIdentity chapter,
        IReadOnlyList<RemotePage> pages)
    {
        var input = new StringBuilder()
            .Append(DrakeScansContract.Version).Append('|')
            .Append(chapter.SourceId).Append('|')
            .Append(chapter.Title.TitleId).Append('|')
            .Append(chapter.ChapterId).Append('|')
            .Append(chapter.Group.GroupId);
        foreach (var page in pages) input.Append('|').Append(page.Ordinal).Append('=').Append(page.Url);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }

    private static string DetectFormat(byte[] payload)
    {
        if (payload.Length >= 12
            && payload[0] == 0x52 && payload[1] == 0x49 && payload[2] == 0x46 && payload[3] == 0x46
            && payload[8] == 0x57 && payload[9] == 0x45 && payload[10] == 0x42 && payload[11] == 0x50)
        {
            return "webp";
        }
        if (payload.Length >= 4 && payload[0] == 0x89 && payload[1] == 0x50
            && payload[2] == 0x4E && payload[3] == 0x47) return "png";
        if (payload.Length >= 3 && payload[0] == 0xFF && payload[1] == 0xD8
            && payload[2] == 0xFF) return "jpg";
        throw new DrakeScansContractException("Drake Scans page payload is not a recognized image.");
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Citadel-MangaReader/2.1 (+authorized-provider-integration)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }
}
