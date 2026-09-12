using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.ManualUrl;

public sealed class DynamicManualContractException(string message) : InvalidOperationException(message);

/// <summary>
/// Hidden source adapter for Manual URL. It reads only the URL stored in the
/// title/chapter identity and therefore remains usable by the durable Queue
/// after the Manual screen has closed or the app has restarted.
/// </summary>
public sealed class DynamicManualSource : IMangaSource
{
    public const string SourceId = "manual-url";
    private const int MaximumHtmlBytes = 8 * 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly ProxyHttpTransport? _transport;
    private readonly ConcurrentDictionary<string, DynamicManualTitlePage> _titleCache =
        new(StringComparer.Ordinal);

    public DynamicManualSource() : this(SharedClient, null)
    {
    }

    internal DynamicManualSource(HttpClient client) : this(client, null)
    {
    }

    internal DynamicManualSource(ProxyHttpTransport transport) : this(SharedClient, transport)
    {
    }

    private DynamicManualSource(HttpClient client, ProxyHttpTransport? transport)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transport = transport;
    }

    public static RemoteSourceGroup Group { get; } = new(
        new RemoteGroupIdentity(SourceId, SourceId),
        "Manual URL");

    public string Id => SourceId;

    public string DisplayName => "Manual URL";

    public MangaSourceCapabilities Capabilities { get; } = new(false, false, [], false);

    public async Task<RemoteTitleDetail?> TryResolveAsync(Uri url, CancellationToken cancellationToken)
    {
        ValidateHttpUrl(url, "title");
        var html = await SendHtmlAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        var page = DynamicManualHtmlParser.TryParseTitle(url, html);
        if (page is null) return null;
        _titleCache[page.Detail.Summary.Identity.TitleId] = page;
        return page.Detail;
    }

    public Task<RemoteCatalogPage> BrowseAsync(RemoteBrowseRequest request, CancellationToken cancellationToken) =>
        throw new DynamicManualContractException("Manual URL tidak menyediakan catalog browse.");

    public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken) =>
        throw new DynamicManualContractException("Manual URL tidak menyediakan filter lookup.");

    public async Task<RemoteTitleDetail> GetTitleAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        ValidateTitle(title);
        var page = await LoadTitlePageAsync(title, cancellationToken).ConfigureAwait(false);
        return page.Detail;
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
        var page = await LoadTitlePageAsync(title, cancellationToken).ConfigureAwait(false);
        if (page.Chapters.Count > 0) return page.Chapters;

        var titleUrl = new Uri(title.TitleId, UriKind.Absolute);
        var ajaxUrl = new Uri(titleUrl.AbsoluteUri.TrimEnd('/') + "/ajax/chapters/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, ajaxUrl)
        {
            Content = new FormUrlEncodedContent([]),
        };
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = titleUrl;
        var html = await SendHtmlAsync(request, cancellationToken).ConfigureAwait(false);
        var chapters = DynamicManualHtmlParser.ParseChapters(html, titleUrl, title);
        if (chapters.Count == 0)
        {
            throw new DynamicManualContractException("Halaman title tidak memiliki daftar chapter yang dapat dibaca.");
        }

        _titleCache[title.TitleId] = page with { Chapters = chapters };
        return chapters;
    }

    public async Task<RemoteChapterManifest> GetManifestAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken)
    {
        ValidateChapter(chapter);
        var chapterUrl = new Uri(chapter.ChapterId, UriKind.Absolute);
        var html = await SendHtmlAsync(new HttpRequestMessage(HttpMethod.Get, chapterUrl), cancellationToken)
            .ConfigureAwait(false);
        var urls = DynamicManualHtmlParser.ParsePages(chapterUrl, html);
        var pages = urls.Select((url, ordinal) =>
            new RemotePage(ordinal, url, url, null, null)).ToArray();
        return new RemoteChapterManifest(
            chapter,
            pages,
            ManifestHash(chapter, pages),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Referer"] = chapterUrl.AbsoluteUri,
            });
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

    private async Task<DynamicManualTitlePage> LoadTitlePageAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        if (_titleCache.TryGetValue(title.TitleId, out var cached)) return cached;
        var url = new Uri(title.TitleId, UriKind.Absolute);
        var html = await SendHtmlAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        var page = DynamicManualHtmlParser.TryParseTitle(url, html)
            ?? throw new DynamicManualContractException("URL tidak memiliki signature halaman manga yang didukung.");
        _titleCache[title.TitleId] = page;
        return page;
    }

    private async Task<string> SendHtmlAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var response = _transport is null
                   ? await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
                   : await _transport.SendAsync("manual-url", _client, request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            if (response.Content.Headers.ContentLength is > MaximumHtmlBytes)
            {
                throw new DynamicManualContractException("Manual URL response melebihi batas 8 MiB.");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(html) > MaximumHtmlBytes)
            {
                throw new DynamicManualContractException("Manual URL response melebihi batas 8 MiB.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new DynamicManualContractException(
                    $"Manual URL request gagal dengan HTTP {(int)response.StatusCode}.");
            }
            if (LooksLikeChallenge(html))
            {
                throw new DynamicManualContractException("Manual URL mengembalikan browser challenge.");
            }

            return html;
        }
    }

    private static void ValidateTitle(RemoteTitleIdentity title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!string.Equals(title.SourceId, SourceId, StringComparison.Ordinal)
            || !Uri.TryCreate(title.TitleId, UriKind.Absolute, out var url))
        {
            throw new DynamicManualContractException("Invalid Manual URL title identity.");
        }
        ValidateHttpUrl(url, "title");
    }

    private static void ValidateGroup(RemoteGroupIdentity group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Equals(group, Group.Identity))
        {
            throw new DynamicManualContractException("Invalid Manual URL source group.");
        }
    }

    private static void ValidateChapter(RemoteChapterIdentity chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ValidateTitle(chapter.Title);
        ValidateGroup(chapter.Group);
        if (!string.Equals(chapter.SourceId, SourceId, StringComparison.Ordinal)
            || !Uri.TryCreate(chapter.ChapterId, UriKind.Absolute, out var url))
        {
            throw new DynamicManualContractException("Invalid Manual URL chapter identity.");
        }
        ValidateHttpUrl(url, "chapter");
    }

    private static void ValidateHttpUrl(Uri? url, string label)
    {
        if (url is null || !url.IsAbsoluteUri || url.Scheme is not ("https" or "http"))
        {
            throw new DynamicManualContractException($"Manual URL {label} harus berupa URL http atau https.");
        }
    }

    private static bool LooksLikeChallenge(string html) =>
        html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Sorry, you have been blocked", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Cloudflare Ray ID", StringComparison.OrdinalIgnoreCase);

    private static string ManifestHash(
        RemoteChapterIdentity chapter,
        IReadOnlyList<RemotePage> pages)
    {
        var builder = new StringBuilder();
        builder.Append(SourceId).Append('|').Append(chapter.ChapterId);
        foreach (var page in pages) builder.Append('|').Append(page.Ordinal).Append('=').Append(page.Url);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string DetectFormat(byte[] payload)
    {
        if (payload.Length >= 4
            && payload[0] == 0x89 && payload[1] == 0x50
            && payload[2] == 0x4E && payload[3] == 0x47) return "png";
        if (payload.Length >= 3
            && payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF) return "jpg";
        if (payload.Length >= 12
            && payload[0] == 0x52 && payload[1] == 0x49 && payload[2] == 0x46 && payload[3] == 0x46
            && payload[8] == 0x57 && payload[9] == 0x45 && payload[10] == 0x42 && payload[11] == 0x50) return "webp";
        if (payload.Length >= 3
            && payload[0] == 0x47 && payload[1] == 0x49 && payload[2] == 0x46) return "gif";
        throw new DynamicManualContractException("Manual URL page payload bukan gambar yang valid.");
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
