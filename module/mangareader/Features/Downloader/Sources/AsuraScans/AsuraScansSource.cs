using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Sources.AsuraScans;

public sealed class AsuraScansSource : IMangaSource
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly ProxyHttpTransport? _transport;
    private readonly SemaphoreSlim _sitemapGate = new(1, 1);
    private string? _chapterSitemap;

    public AsuraScansSource() : this(SharedClient, null) { }
    internal AsuraScansSource(HttpClient client) : this(client, null) { }
    internal AsuraScansSource(ProxyHttpTransport transport) : this(SharedClient, transport) { }
    private AsuraScansSource(HttpClient client, ProxyHttpTransport? transport)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transport = transport;
    }

    public static RemoteSourceGroup Group { get; } = new(
        new RemoteGroupIdentity(AsuraScansContract.SourceId, AsuraScansContract.GroupId),
        AsuraScansContract.DisplayName);
    public string Id => AsuraScansContract.SourceId;
    public string DisplayName => AsuraScansContract.DisplayName;
    public MangaSourceCapabilities Capabilities { get; } = new(true, false, [], false);

    public async Task<RemoteCatalogPage> BrowseAsync(RemoteBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request.Page));
        if (request.Filter is not null and not AsuraScansBrowseQuery)
            throw new AsuraScansContractException("AsuraScans received a filter owned by another provider.");
        var offset = (request.Page - 1) * AsuraScansContract.PageSize;
        var pagination = "offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&limit=" + AsuraScansContract.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var route = string.IsNullOrWhiteSpace(request.Query)
            ? "/api/series?" + pagination
            : "/api/search?q=" + Uri.EscapeDataString(request.Query.Trim()) + "&" + pagination;
        return AsuraScansJsonParser.ParseCatalog(await GetTextAsync(AsuraScansContract.ApiBaseUrl + route, cancellationToken), request.Page);
    }

    public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(RemoteLookupKind kind, string query, CancellationToken cancellationToken) =>
        throw new AsuraScansContractException($"AsuraScans does not expose the '{kind}' lookup in this provider version.");

    public async Task<RemoteTitleDetail> GetTitleAsync(RemoteTitleIdentity title, CancellationToken cancellationToken)
    {
        ValidateTitle(title);
        return AsuraScansJsonParser.ParseTitle(
            await GetTextAsync(AsuraScansContract.ApiBaseUrl + "/api/series/" + Uri.EscapeDataString(RouteSlug(title)), cancellationToken), title);
    }

    internal async Task<RemoteTitleDetail> ResolveTitleAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var identity = new RemoteTitleIdentity(AsuraScansContract.SourceId, slug, slug, slug);
        return await GetTitleAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(RemoteTitleIdentity title, CancellationToken cancellationToken)
    {
        ValidateTitle(title); cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RemoteSourceGroup>>([Group]);
    }

    public async Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(RemoteTitleIdentity title, RemoteGroupIdentity group, CancellationToken cancellationToken)
    {
        ValidateTitle(title); ValidateGroup(group);
        var fromTitle = AsuraScansHtmlParser.ParseChapters(
            await GetHtmlAsync(TitlePath(title), cancellationToken), title, group);
        if (fromTitle.Count > 0) return fromTitle;

        var fromSitemap = AsuraScansHtmlParser.ParseSitemapChapters(
            await GetChapterSitemapAsync(cancellationToken), title, group);
        if (fromSitemap.Count == 0)
            throw new AsuraScansContractException("AsuraScans title has no chapter URLs in its documented title page or sitemap.");
        return fromSitemap;
    }

    public async Task<RemoteChapterManifest> GetManifestAsync(RemoteChapterIdentity chapter, CancellationToken cancellationToken)
    {
        ValidateChapter(chapter);
        var route = ChapterPath(chapter);
        var urls = AsuraScansHtmlParser.ParsePages(await GetHtmlAsync(route, cancellationToken));
        var pages = urls.Select((url, ordinal) => new RemotePage(ordinal, url, url, null, null)).ToArray();
        return new RemoteChapterManifest(chapter, pages, Hash(chapter, pages), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = AsuraScansContract.BaseUrl + route,
        });
    }

    public Task<RemotePageImage> TransformPageAsync(RemotePage page, byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page); ArgumentNullException.ThrowIfNull(payload); cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length < 12 || payload[0] != 0x52 || payload[1] != 0x49 || payload[2] != 0x46 || payload[3] != 0x46 || payload[8] != 0x57 || payload[9] != 0x45 || payload[10] != 0x42 || payload[11] != 0x50)
            throw new AsuraScansContractException("AsuraScans page payload is not WebP.");
        return Task.FromResult(new RemotePageImage(payload, "webp"));
    }

    public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(RemoteChapterIdentity chapter, CancellationToken cancellationToken)
    {
        ValidateChapter(chapter); cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RemoteAlternateChapter>>([]);
    }

    private async Task<string> GetHtmlAsync(string route, CancellationToken token) => await GetTextAsync(AsuraScansContract.BaseUrl + route, token);

    private async Task<string> GetChapterSitemapAsync(CancellationToken token)
    {
        if (_chapterSitemap is not null) return _chapterSitemap;
        await _sitemapGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return _chapterSitemap ??= await GetTextAsync(
                AsuraScansContract.BaseUrl + "/sitemap-chapters-1.xml", token).ConfigureAwait(false);
        }
        finally
        {
            _sitemapGate.Release();
        }
    }
    private async Task<string> GetTextAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(AsuraScansContract.BaseUrl + "/");
        using var response = _transport is null
            ? await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)
            : await _transport.SendAsync("asura", _client, request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new AsuraScansContractException($"AsuraScans request failed with HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is > AsuraScansContract.MaximumResponseBytes) throw new AsuraScansContractException("AsuraScans response exceeds 8 MiB.");
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(body) > AsuraScansContract.MaximumResponseBytes) throw new AsuraScansContractException("AsuraScans response exceeds 8 MiB.");
        return body;
    }

    // Queue handoffs preserve TitleId but intentionally do not persist the optional Slug.
    // Asura's TitleId is its route slug, so use it as the durable fallback.
    private static string RouteSlug(RemoteTitleIdentity title) => !string.IsNullOrWhiteSpace(title.Slug) ? title.Slug : title.TitleId;
    private static string TitlePath(RemoteTitleIdentity title) => "/comics/" + Uri.EscapeDataString(RouteSlug(title));
    private static string ChapterPath(RemoteChapterIdentity chapter) => TitlePath(chapter.Title) + "/chapter/" + Uri.EscapeDataString(chapter.ChapterId);
    private static string Hash(RemoteChapterIdentity chapter, IReadOnlyList<RemotePage> pages) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', pages.Select(page => page.Url).Prepend(AsuraScansContract.Version + "|" + chapter.ChapterId)))));
    private static void ValidateTitle(RemoteTitleIdentity title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!string.Equals(title.SourceId, AsuraScansContract.SourceId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(title.TitleId)) throw new AsuraScansContractException("Invalid AsuraScans title identity.");
    }
    private static void ValidateGroup(RemoteGroupIdentity group) { ArgumentNullException.ThrowIfNull(group); if (!Equals(group, Group.Identity)) throw new AsuraScansContractException("Invalid AsuraScans source group."); }
    private static void ValidateChapter(RemoteChapterIdentity chapter) { ArgumentNullException.ThrowIfNull(chapter); ValidateTitle(chapter.Title); ValidateGroup(chapter.Group); if (!string.Equals(chapter.SourceId, AsuraScansContract.SourceId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(chapter.ChapterId)) throw new AsuraScansContractException("Invalid AsuraScans chapter identity."); }
    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140 Safari/537.36");
        return client;
    }
}
