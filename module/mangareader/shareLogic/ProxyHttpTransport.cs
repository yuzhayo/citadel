using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.IO;
using CitadelBridge;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// Applies one lane-owned lease to provider-owned requests. Providers retain
/// their URLs, headers, parsers, and retry decisions.
/// </summary>
internal sealed class ProxyHttpTransport(ProxyPoolAdapter pool) : IDisposable
{
    private const int MaxProxyGetAttempts = 3;
    // Cover hosts can reject one proxy without that proxy being unusable for
    // chapter traffic. A cover therefore gets a bounded turn on every HTTP
    // endpoint before a retry cycle begins; it never writes to lane quarantine.
    private const int MaxCoverAttemptsPerProxy = 4;
    private static readonly HttpRequestOptionsKey<ProxyLease> LeaseOption =
        new("Citadel.MangaReader.ProxyLease");

    private readonly ProxyPoolAdapter _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private int _disposed;

    public bool IsProxyMode => _pool.IsProxyMode;
    internal ProxyPoolAdapter Pool => _pool;

    // Explicit callers own the reservation through full body consumption. No retries here.
    internal Task<HttpResponseMessage> SendWithLeaseAsync(
        string owner, HttpClient defaults, HttpRequestMessage request,
        ProxyLease lease, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CopyDefaultHeaders(defaults, request);
        var key = owner + "\n" + lease.Endpoint.Canonical;
        var client = _clients.GetOrAdd(key, _ => CreateProxyClient(lease.Endpoint, Timeout.InfiniteTimeSpan));
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    public Task<HttpResponseMessage> SendAsync(
        string owner,
        HttpClient directClient,
        HttpRequestMessage request,
        HttpCompletionOption completion,
        CancellationToken cancellationToken) =>
        SendAsync(owner, directClient, request, completion, _pool.IsProxyMode, cancellationToken);

    internal async Task<HttpResponseMessage> SendAsync(
        string owner,
        HttpClient directClient,
        HttpRequestMessage request,
        HttpCompletionOption completion,
        bool useProxy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(directClient);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var lease = _pool.Acquire(ProxyTarget.Http, useProxy);
        if (lease is null)
        {
            return await directClient.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }

        CopyDefaultHeaders(directClient, request);
        request.Options.Set(LeaseOption, lease);
        var reservation = await _pool.Reservations.ReserveAsync(owner,
            [lease.Endpoint], cancellationToken).ConfigureAwait(false);
        var key = owner + "\n" + lease.Endpoint.Canonical;
        var client = _clients.GetOrAdd(key, _ => CreateProxyClient(lease.Endpoint, directClient.Timeout));
        try
        {
            var response = await client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            response.Content = new ReservedContent(response.Content, reservation);
            return response;
        }
        catch (HttpRequestException)
        {
            reservation.Dispose();
            _pool.ReportFailure(lease);
            throw;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reservation.Dispose();
            _pool.ReportFailure(lease);
            throw;
        }
        catch { reservation.Dispose(); throw; }
    }

    /// <summary>
    /// Reports a failure discovered after response headers were returned. The
    /// request carries its own lease so a stalled response body quarantines the
    /// exact proxy that supplied it instead of an unrelated next lease.
    /// </summary>
    internal void ReportResponseFailure(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.TryGetValue(LeaseOption, out var lease))
        {
            _pool.ReportFailure(lease);
        }
    }

    public async Task<byte[]> GetByteArrayAsync(
        string owner,
        HttpClient directClient,
        string url,
        CancellationToken cancellationToken)
    {
        var attempts = _pool.IsProxyMode ? MaxProxyGetAttempts : 1;
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendAsync(
                    owner,
                    directClient,
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (_pool.IsProxyMode && IsRetryableGetFailure(ex))
            {
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new HttpRequestException($"{owner}: proxy GET failed without an error.");
    }

    /// <summary>
    /// Fetches a provider-owned image through a unique pool reservation. Every
    /// concurrent caller receives a different healthy endpoint; callers beyond
    /// the pool size wait instead of sharing one proxy concurrently. Provider
    /// headers remain request-local and direct mode remains one request.
    /// </summary>
    internal async Task<byte[]> GetByteArrayWithHeadersAsync(
        string owner,
        HttpClient directClient,
        string url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(directClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(headers);

        if (!_pool.IsProxyMode)
        {
            using var request = CreateCoverRequest(url, headers);
            using var response = await directClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        // A cycle gives every endpoint at most four attempts. On retryable
        // failures the cycle restarts, so a slow CDN route can recover without
        // permanently excluding the endpoint from Downloader's other lanes.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = _pool.Candidates(ProxyTarget.Http);
            if (candidates.Count == 0)
                throw new ProxyPoolException("PROXY_POOL_INCOMPATIBLE", "no HTTP-compatible endpoint for cover requests");

            var attemptsByEndpoint = new Dictionary<string, int>(StringComparer.Ordinal);
            Exception? lastFailure = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateOrder = candidates
                    // Endpoints not yet tried are deliberately first, so one
                    // bad cover response rotates before any endpoint repeats.
                    .Where(endpoint => attemptsByEndpoint.GetValueOrDefault(ProxyLeaseRegistry.Key(endpoint))
                        < MaxCoverAttemptsPerProxy)
                    .OrderBy(endpoint => attemptsByEndpoint.GetValueOrDefault(ProxyLeaseRegistry.Key(endpoint)))
                    .ThenBy(endpoint => endpoint.Canonical, StringComparer.Ordinal)
                    .ToArray();
                if (candidateOrder.Length == 0) break;

                using var request = CreateCoverRequest(url, headers);
                try
                {
                    using var response = await SendWithDistinctProxyAsync(
                        owner,
                        directClient,
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        candidateOrder,
                        quarantineOnTransportFailure: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsRetryableCoverFailure(ex))
                {
                    lastFailure = ex;
                    if (request.Options.TryGetValue(LeaseOption, out var lease))
                    {
                        var endpointKey = ProxyLeaseRegistry.Key(lease.Endpoint);
                        attemptsByEndpoint[endpointKey] = attemptsByEndpoint.GetValueOrDefault(endpointKey) + 1;
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            if (lastFailure is null)
                throw new HttpRequestException($"{owner}: cover request ended without an attempt.");
        }
    }

    private async Task<HttpResponseMessage> SendWithDistinctProxyAsync(
        string owner,
        HttpClient directClient,
        HttpRequestMessage request,
        HttpCompletionOption completion,
        IReadOnlyList<ProxyEndpoint>? candidates,
        bool quarantineOnTransportFailure,
        CancellationToken cancellationToken)
    {
        if (!_pool.IsProxyMode)
        {
            return await directClient.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }

        candidates ??= _pool.AvailableCandidates(ProxyTarget.Http);
        var reservation = await _pool.Reservations.ReserveAsync(owner, candidates, cancellationToken)
            .ConfigureAwait(false);
        var lease = reservation.Lease;
        CopyDefaultHeaders(directClient, request);
        request.Options.Set(LeaseOption, lease);
        var key = owner + "\n" + lease.Endpoint.Canonical;
        var client = _clients.GetOrAdd(key, _ => CreateProxyClient(lease.Endpoint, directClient.Timeout));
        try
        {
            var response = await client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            response.Content = new ReservedContent(response.Content, reservation);
            return response;
        }
        catch (HttpRequestException)
        {
            reservation.Dispose();
            if (quarantineOnTransportFailure) _pool.ReportFailure(lease);
            throw;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reservation.Dispose();
            if (quarantineOnTransportFailure) _pool.ReportFailure(lease);
            throw;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }

    private static HttpClient CreateProxyClient(ProxyEndpoint endpoint, TimeSpan timeout)
    {
        var proxy = new WebProxy(endpoint.Server);
        if (endpoint.Username is not null)
        {
            proxy.Credentials = new NetworkCredential(endpoint.Username, endpoint.Password ?? string.Empty);
        }
        var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private sealed class ReservedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly ProxyReservation _reservation;
        public ReservedContent(HttpContent inner, ProxyReservation reservation)
        {
            _inner = inner; _reservation = reservation;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            _inner.CopyToAsync(stream, token);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => _inner.ReadAsStreamAsync(token);
        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _inner.Dispose(); } finally { _reservation.Dispose(); } }
            base.Dispose(disposing);
        }
    }

    private static bool IsRetryableGetFailure(Exception error) =>
        error is HttpRequestException or TimeoutException
        || error is TaskCanceledException;

    private static bool IsRetryableCoverFailure(Exception error)
    {
        if (error is TimeoutException or TaskCanceledException) return true;
        if (error is not HttpRequestException { StatusCode: { } status })
        {
            return error is HttpRequestException;
        }

        return ShouldQuarantineCoverResponse(status);
    }

    private static bool ShouldQuarantineCoverResponse(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)status >= 500;

    private static HttpRequestMessage CreateCoverRequest(
        string url, IReadOnlyDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }

    private static void CopyDefaultHeaders(HttpClient source, HttpRequestMessage request)
    {
        foreach (var header in source.DefaultRequestHeaders)
        {
            if (!request.Headers.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}
