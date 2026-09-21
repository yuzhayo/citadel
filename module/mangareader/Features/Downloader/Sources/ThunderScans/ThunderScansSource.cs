using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Sources.ThunderScans;

public sealed class ThunderScansSource : IMangaSource
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly ProxyHttpTransport? _transport;
    private readonly ThunderScansTitleAdapter _titleAdapter;

    public ThunderScansSource() : this(SharedClient, null) { }
    internal ThunderScansSource(HttpClient client) : this(client, null) { }
    internal ThunderScansSource(ProxyHttpTransport transport) : this(SharedClient, transport) { }
    private ThunderScansSource(HttpClient client, ProxyHttpTransport? transport)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transport = transport;
        _titleAdapter = new ThunderScansTitleAdapter(GetTextAsync, GetSitemapLocationsAsync);
    }

    public static RemoteSourceGroup Group { get; } = new(new RemoteGroupIdentity(ThunderScansContract.SourceId, ThunderScansContract.GroupId), ThunderScansContract.DisplayName);
    public string Id => ThunderScansContract.SourceId;
    public string DisplayName => ThunderScansContract.DisplayName;
    public MangaSourceCapabilities Capabilities { get; } = new(true, false, [], false);

    public async Task<RemoteCatalogPage> BrowseAsync(RemoteBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Page < 1) throw new ArgumentOutOfRangeException(nameof(request.Page));
        if (request.Filter is not null and not ThunderScansBrowseQuery) throw new ThunderScansContractException("ThunderScans received a filter owned by another provider.");
        var keyword = NormalizeSearchText(request.Query);
        var matches = (await _titleAdapter.GetTitleRoutesAsync(cancellationToken).ConfigureAwait(false))
            .Where(slug => keyword is null || slug.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(slug => slug, StringComparer.OrdinalIgnoreCase).ToArray();
        var page = matches.Skip((request.Page - 1) * ThunderScansContract.PageSize).Take(ThunderScansContract.PageSize).ToArray();
        var details = await Task.WhenAll(page.Select(async slug =>
        {
            var identity = new RemoteTitleIdentity(ThunderScansContract.SourceId, slug, slug, slug);
            return (await GetTitleAsync(identity, cancellationToken).ConfigureAwait(false)).Summary;
        })).ConfigureAwait(false);
        return new RemoteCatalogPage(details, matches.Length, request.Page, request.Page * ThunderScansContract.PageSize < matches.Length);
    }

    internal static string? NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = new StringBuilder(value.Length);
        var separatorPending = false;
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && normalized.Length > 0) normalized.Append('-');
                normalized.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = normalized.Length > 0;
            }
        }

        return normalized.Length == 0 ? null : normalized.ToString();
    }

    public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(RemoteLookupKind kind, string query, CancellationToken cancellationToken) => throw new ThunderScansContractException($"ThunderScans does not expose the '{kind}' lookup in this provider version.");
    public async Task<RemoteTitleDetail> GetTitleAsync(RemoteTitleIdentity title, CancellationToken cancellationToken) { ValidateTitle(title); return await _titleAdapter.GetDetailAsync(title, cancellationToken).ConfigureAwait(false); }
    internal async Task<RemoteTitleDetail> ResolveTitleAsync(string slug, CancellationToken cancellationToken) { ArgumentException.ThrowIfNullOrWhiteSpace(slug); var identity = new RemoteTitleIdentity(ThunderScansContract.SourceId, slug, slug, slug); return await GetTitleAsync(identity, cancellationToken).ConfigureAwait(false); }
    public Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(RemoteTitleIdentity title, CancellationToken cancellationToken) { ValidateTitle(title); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<RemoteSourceGroup>>([Group]); }
    public async Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(RemoteTitleIdentity title, RemoteGroupIdentity group, CancellationToken cancellationToken)
    {
        ValidateTitle(title); ValidateGroup(group);
        return await _titleAdapter.GetChaptersAsync(title, group, cancellationToken).ConfigureAwait(false);
    }
    public async Task<RemoteChapterManifest> GetManifestAsync(RemoteChapterIdentity chapter, CancellationToken cancellationToken)
    {
        ValidateChapter(chapter); var route = ChapterPath(chapter); var urls = ThunderScansHtmlParser.ParsePages(await GetHtmlAsync(route, cancellationToken));
        var pages = urls.Select((url, ordinal) => new RemotePage(ordinal, url, url, null, null)).ToArray();
        return new RemoteChapterManifest(chapter, pages, Hash(chapter, pages), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Referer"] = ThunderScansContract.BaseUrl + route });
    }
    public Task<RemotePageImage> TransformPageAsync(RemotePage page, byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page); ArgumentNullException.ThrowIfNull(payload); cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length < 3 || payload[0] != 0xFF || payload[1] != 0xD8 || payload[2] != 0xFF) throw new ThunderScansContractException("ThunderScans page payload is not JPEG.");
        return Task.FromResult(new RemotePageImage(payload, "jpg"));
    }
    public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(RemoteChapterIdentity chapter, CancellationToken cancellationToken) { ValidateChapter(chapter); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<RemoteAlternateChapter>>([]); }

    private async Task<IReadOnlyList<string>> GetSitemapLocationsAsync(CancellationToken token)
    {
        try
        {
            var documented = ThunderScansHtmlParser.ParseSitemapLocations(
                await GetTextAsync(ThunderScansContract.BaseUrl + "/sitemap_index.xml", token).ConfigureAwait(false));
            if (documented.Count > 0) return documented;
        }
        catch (ThunderScansContractException) { }

        return ThunderScansHtmlParser.ParseSitemapLocations(
            await GetTextAsync(ThunderScansContract.BaseUrl + "/wp-sitemap.xml", token).ConfigureAwait(false));
    }
    private async Task<string> GetHtmlAsync(string route, CancellationToken token) => await GetTextAsync(ThunderScansContract.BaseUrl + route, token);
    private async Task<string> GetTextAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Referrer = new Uri(ThunderScansContract.BaseUrl + "/");
        using var response = _transport is null ? await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false) : await _transport.SendAsync("thunder", _client, request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ThunderScansContractException($"ThunderScans request failed with HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is > ThunderScansContract.MaximumResponseBytes) throw new ThunderScansContractException("ThunderScans response exceeds 8 MiB.");
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(body) > ThunderScansContract.MaximumResponseBytes) throw new ThunderScansContractException("ThunderScans response exceeds 8 MiB.");
        return body;
    }
    private static string RouteSlug(RemoteTitleIdentity title) => !string.IsNullOrWhiteSpace(title.Slug) ? title.Slug : title.TitleId;
    private static string TitlePath(RemoteTitleIdentity title) => "/comics/" + Uri.EscapeDataString(RouteSlug(title)) + "/";
    private static string ChapterPath(RemoteChapterIdentity chapter) =>
        "/" + Uri.EscapeDataString(chapter.ChapterId.Trim('/')) + "/";
    private static string Hash(RemoteChapterIdentity chapter, IReadOnlyList<RemotePage> pages) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', pages.Select(page => page.Url).Prepend(ThunderScansContract.Version + "|" + chapter.ChapterId)))));
    private static void ValidateTitle(RemoteTitleIdentity title) { ArgumentNullException.ThrowIfNull(title); if (!string.Equals(title.SourceId, ThunderScansContract.SourceId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(title.TitleId)) throw new ThunderScansContractException("Invalid ThunderScans title identity."); }
    private static void ValidateGroup(RemoteGroupIdentity group) { ArgumentNullException.ThrowIfNull(group); if (!Equals(group, Group.Identity)) throw new ThunderScansContractException("Invalid ThunderScans source group."); }
    private static void ValidateChapter(RemoteChapterIdentity chapter) { ArgumentNullException.ThrowIfNull(chapter); ValidateTitle(chapter.Title); ValidateGroup(chapter.Group); if (!string.Equals(chapter.SourceId, ThunderScansContract.SourceId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(chapter.ChapterId)) throw new ThunderScansContractException("Invalid ThunderScans chapter identity."); }
    private static HttpClient CreateClient() { var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(45) }; client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140 Safari/537.36"); return client; }
}
