using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>
/// Why one page attempt ended. The classes are deliberately distinct because
/// they drive different policy: some are retryable, some are not, and only some
/// may use the single browser fallback.
/// </summary>
public enum PageFetchOutcome
{
    /// <summary>Bytes are in staging and validated as an image.</summary>
    Stored,

    /// <summary>404/410 — the page is gone. Never a browser fallback.</summary>
    NotFound,

    /// <summary>401/403 — the native route lost the session. Fallback eligible.</summary>
    Rejected,

    /// <summary>429 or 5xx — retry on schedule, no fallback, no group change.</summary>
    Throttled,

    /// <summary>DNS/TLS/connectivity failure. Fallback eligible.</summary>
    NetworkFailed,

    /// <summary>An HTML or otherwise non-image challenge payload. Fallback eligible.</summary>
    Challenge,

    /// <summary>Declared or observed size exceeded the page bound.</summary>
    TooLarge,
}

public sealed record PageFetchResult(
    PageFetchOutcome Outcome,
    string? StoredPath,
    long Bytes,
    string? Sha256,
    string Format,
    string? Detail,
    bool UsedBrowserFallback)
{
    public bool Succeeded => Outcome == PageFetchOutcome.Stored;

    /// <summary>Exactly the outcomes allowed one browser-context retry.</summary>
    public bool IsFallbackEligible => Outcome is PageFetchOutcome.NetworkFailed
        or PageFetchOutcome.Rejected
        or PageFetchOutcome.Challenge;
}

/// <summary>
/// One page attempt: bounded native HTTP streaming first, then at most one
/// browser-context fetch for the outcomes that only the browser can satisfy.
/// Bytes are written straight into staging and never carried through the pyhost
/// protocol; only evidence comes back from the browser.
/// </summary>
public sealed class PageTransport : IDisposable
{
    private const long MaximumPageBytes = 25 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    private readonly DownloaderPyHostClient _browser;
    private readonly string _stagingRoot;
    private int _disposed;

    public PageTransport(DownloaderPyHostClient browser, string stagingRoot)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot);
    }

    /// <summary>Absolute root every staged page lives under.</summary>
    public string StagingRoot => _stagingRoot;

    public async Task<PageFetchResult> FetchAsync(
        RemotePage page,
        string relativePath,
        string? referer,
        IReadOnlyDictionary<string, string>? manifestHeaders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var target = _browser.ResolveContained(relativePath);

        var native = await FetchNativeAsync(page, target, referer, manifestHeaders, cancellationToken)
            .ConfigureAwait(false);
        if (native.Succeeded || !native.IsFallbackEligible)
        {
            return native;
        }

        // At most one browser-context fetch per attempt, and only for the
        // outcomes the browser can actually satisfy. A successful fallback
        // still passes the same byte-format and size validation.
        var fallback = await FetchThroughBrowserAsync(page, target, referer, cancellationToken)
            .ConfigureAwait(false);
        return fallback with { UsedBrowserFallback = true };
    }

    private async Task<PageFetchResult> FetchNativeAsync(
        RemotePage page,
        string target,
        string? referer,
        IReadOnlyDictionary<string, string>? manifestHeaders,
        CancellationToken cancellationToken)
    {
        var temporary = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, page.Url);
            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
            }

            if (manifestHeaders is not null)
            {
                foreach (var header in manifestHeaders)
                {
                    if (string.Equals(header.Key, "Referer", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await Client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                return Failure(PageFetchOutcome.NetworkFailed, exception.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(PageFetchOutcome.NetworkFailed, "native page request timed out");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 404 or 410)
                {
                    return Failure(PageFetchOutcome.NotFound, $"HTTP {status}");
                }

                if (status is 401 or 403)
                {
                    return Failure(PageFetchOutcome.Rejected, $"HTTP {status}");
                }

                if (status == 429 || status >= 500)
                {
                    return Failure(PageFetchOutcome.Throttled, $"HTTP {status}");
                }

                if (status is < 200 or >= 300)
                {
                    return Failure(PageFetchOutcome.NetworkFailed, $"HTTP {status}");
                }

                if (response.Content.Headers.ContentLength is > MaximumPageBytes)
                {
                    return Failure(PageFetchOutcome.TooLarge, "declared size exceeds the page bound");
                }

                long written;
                try
                {
                    written = await StreamToAsync(
                        response.Content, temporary, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                {
                    return Failure(PageFetchOutcome.NetworkFailed, exception.Message);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Failure(PageFetchOutcome.NetworkFailed, "native page stream timed out");
                }

                var bytes = await File.ReadAllBytesAsync(temporary, cancellationToken)
                    .ConfigureAwait(false);
                string format;
                try
                {
                    format = DetectImageFormat(bytes);
                }
                catch (InvalidDataException exception)
                {
                    // An HTML or otherwise non-image body is a challenge, not a
                    // missing page, and it is fallback eligible.
                    return Failure(PageFetchOutcome.Challenge, exception.Message);
                }

                File.Move(temporary, target, overwrite: true);
                return new PageFetchResult(
                    PageFetchOutcome.Stored,
                    target,
                    written,
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    format,
                    null,
                    UsedBrowserFallback: false);
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<PageFetchResult> FetchThroughBrowserAsync(
        RemotePage page,
        string target,
        string? referer,
        CancellationToken cancellationToken)
    {
        BrowserFetchEvidence evidence;
        try
        {
            evidence = await _browser
                .FetchToStagingAsync(page.Url, Path.GetRelativePath(_stagingRoot, target), referer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException
                || exception.GetType().Name == "PyHostException")
        {
            return Failure(PageFetchOutcome.NetworkFailed, "browser fallback failed: " + exception.Message);
        }

        var status = evidence.Status;
        if (status is 404 or 410) return Failure(PageFetchOutcome.NotFound, $"HTTP {status}");
        if (status is 401 or 403) return Failure(PageFetchOutcome.Rejected, $"HTTP {status}");
        if (status == 429 || status >= 500) return Failure(PageFetchOutcome.Throttled, $"HTTP {status}");
        if (status is < 200 or >= 300) return Failure(PageFetchOutcome.NetworkFailed, $"HTTP {status}");
        if (evidence.Bytes > MaximumPageBytes) return Failure(PageFetchOutcome.TooLarge, "page exceeds the bound");
        if (evidence.Path is null || !File.Exists(evidence.Path))
        {
            return Failure(PageFetchOutcome.NetworkFailed, "browser fetch reported no staged file");
        }

        // The same validation as native output: bytes must be a real image.
        var bytes = await File.ReadAllBytesAsync(evidence.Path, cancellationToken).ConfigureAwait(false);
        string format;
        try
        {
            format = DetectImageFormat(bytes);
        }
        catch (InvalidDataException exception)
        {
            return Failure(PageFetchOutcome.Challenge, exception.Message);
        }

        if (!string.Equals(evidence.Path, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(evidence.Path, target, overwrite: true);
        }

        return new PageFetchResult(
            PageFetchOutcome.Stored,
            target,
            bytes.Length,
            evidence.Sha256 ?? Convert.ToHexString(SHA256.HashData(bytes)),
            format,
            null,
            UsedBrowserFallback: true);
    }

    private static async Task<long> StreamToAsync(
        HttpContent content,
        string path,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan);

        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;

            total += read;
            if (total > MaximumPageBytes)
            {
                throw new InvalidDataException("page stream exceeded the size bound");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>
    /// Format detected from bytes only — never from a filename or a response
    /// URL. An unrecognized payload is rejected instead of being published.
    /// </summary>
    internal static string DetectImageFormat(byte[] payload)
    {
        if (payload.Length >= 4
            && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
        {
            return "png";
        }

        if (payload.Length >= 3 && payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF)
        {
            return "jpg";
        }

        if (payload.Length >= 12
            && payload[8] == 0x57 && payload[9] == 0x45 && payload[10] == 0x42 && payload[11] == 0x50)
        {
            return "webp";
        }

        if (payload.Length >= 6
            && payload[0] == 0x47 && payload[1] == 0x49 && payload[2] == 0x46)
        {
            return "gif";
        }

        throw new InvalidDataException(
            "payload is not a recognized image; it may be an HTML or error page");
    }

    private static PageFetchResult Failure(PageFetchOutcome outcome, string detail) =>
        new(outcome, null, 0, null, string.Empty, detail, UsedBrowserFallback: false);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // Per-attempt bound; the caller's token carries the job lifetime.
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Citadel-MangaReader-Downloader/1.0");
        return client;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // The HttpClient is static and shared, so it is deliberately not
        // disposed here; the browser client has its own owner.
    }
}
