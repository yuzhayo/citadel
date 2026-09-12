using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.CatalogMirror;

// Bounded fetch/decode/cache of one title cover through the injected HTTP
// client. It never fetches detail/chapters, never bulk-prefetches, never
// changes snapshot or enrichment metadata, never uses the browser, and never
// accepts an unvalidated path. The caller fetches only when no valid cache
// exists; a cover failure returns null and never fails the detail flow.
public sealed class CatalogMirrorCoverCache
{
    public const long MaxCoverBytes = 20_971_520;

    private readonly CatalogMirrorPaths _paths;
    private readonly HttpClient _http;
    private readonly ProxyHttpTransport? _httpTransport;

    public CatalogMirrorCoverCache(CatalogMirrorPaths paths, HttpClient http)
        : this(paths, http, null)
    {
    }

    internal CatalogMirrorCoverCache(
        CatalogMirrorPaths paths,
        HttpClient http,
        ProxyHttpTransport? httpTransport)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _httpTransport = httpTransport;
    }

    /// <summary>
    /// Local cover path, fetching once when no valid cache exists. Returns
    /// null when there is no cover URL, the fetch fails, or the bytes are not
    /// a decodable image. Never throws for provider or network conditions.
    /// </summary>
    public async Task<string?> GetCoverPathAsync(
        string sourceId,
        string titleId,
        string? coverUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);

        if (TryGetCachedPath(sourceId, titleId) is { } cached)
        {
            return cached;
        }

        if (!IsHttpUrl(coverUrl))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await FetchBoundedAsync(coverUrl!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (bytes.Length == 0 || !TryDecodeImage(bytes))
        {
            return null;
        }

        var target = _paths.EnrichmentCoverPath(sourceId, titleId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = _paths.TempPathForAtomicWrite(target);
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return target;
    }

    /// <summary>
    /// Existing valid cache path without any network, or null. Existence and
    /// size pass first; a file that no longer decodes is deleted so the next
    /// open refetches instead of serving poisoned bytes forever.
    /// </summary>
    public string? TryGetCachedPath(string sourceId, string titleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);

        var target = _paths.EnrichmentCoverPath(sourceId, titleId);
        if (!File.Exists(target))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            var length = new FileInfo(target).Length;
            if (length <= 0 || length > MaxCoverBytes)
            {
                return null;
            }

            bytes = File.ReadAllBytes(target);
        }
        catch (Exception)
        {
            return null;
        }

        if (!TryDecodeImage(bytes))
        {
            try
            {
                File.Delete(target);
            }
            catch (Exception)
            {
                // Best effort: an undeletable file simply keeps missing.
            }

            return null;
        }

        return target;
    }

    private async Task<byte[]> FetchBoundedAsync(string coverUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, coverUrl);
        using var response = _httpTransport is null
            ? await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
            : await _httpTransport.SendAsync(
                "catalog-cover",
                _http,
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > MaxCoverBytes)
        {
            return [];
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxCoverBytes)
            {
                return [];
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsHttpUrl(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return false;
        }

        return Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
    }

    /// <summary>
    /// True only when the bytes decode as an image with real dimensions. Any
    /// decoder refusal means the payload is not published.
    /// </summary>
    private static bool TryDecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count != 0
                && decoder.Frames[0].PixelWidth > 0
                && decoder.Frames[0].PixelHeight > 0;
        }
        catch (Exception)
        {
            // A validation probe: refusal is a verdict, never an error.
            return false;
        }
    }
}
