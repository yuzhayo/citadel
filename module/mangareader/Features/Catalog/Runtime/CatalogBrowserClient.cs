using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Catalog.Runtime;

/// <summary>
/// Result of one browser-context fetch that wrote straight into staging. Page
/// bytes never travel through the pyhost protocol: only this evidence does.
/// </summary>
public sealed record CatalogBrowserFetchEvidence(
    int Status,
    long Bytes,
    string? Sha256,
    string ContentType,
    string? Path);

/// <summary>
/// Catalog's own command/payload adapter over the shared pyhost
/// transport. It owns this feature's browser lifetime: the host is started
/// lazily, held only while work needs it, released after bounded idle, and
/// never shares Downloader's or CamoProf's process, profile, or session registry.
///
/// Timeouts are fixed per operation class rather than negotiated per call.
/// </summary>
public sealed class CatalogBrowserClient : IDisposable
{
    /// <summary>Camoufox bootstrap and first navigation.</summary>
    public static readonly TimeSpan BootstrapTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Catalog, detail, lookup, manifest, and browser fallback.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Each single page attempt.</summary>
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);

    private const string PluginName = "mangareader_catalog";
    private const int MaxProxyOpenAttempts = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProxyPoolAdapter? _proxyPool;
    private readonly Func<JsonObject, CancellationToken, Task<JsonObject>>? _openSessionOverride;
    private PyHost? _host;
    private string? _session;
    private string? _sessionProvider;
    private bool? _sessionHeadless;
    private bool _sessionProxyMode;
    private int _showBrowser;
    private DateTimeOffset _lastUsedUtc = DateTimeOffset.UtcNow;
    private int _disposed;

    public CatalogBrowserClient(string stagingRoot) : this(stagingRoot, null)
    {
    }

    internal CatalogBrowserClient(string stagingRoot, ProxyPoolAdapter? proxyPool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        StagingRoot = Path.GetFullPath(stagingRoot);
        _proxyPool = proxyPool;
        Directory.CreateDirectory(StagingRoot);
    }

    internal CatalogBrowserClient(
        string stagingRoot,
        ProxyPoolAdapter proxyPool,
        Func<JsonObject, CancellationToken, Task<JsonObject>> openSession)
        : this(stagingRoot, proxyPool)
    {
        _openSessionOverride = openSession ?? throw new ArgumentNullException(nameof(openSession));
    }

    /// <summary>Absolute root every browser write must stay inside.</summary>
    public string StagingRoot { get; }

    /// <summary>
    /// Whether the next explicit browser-backed action should use a visible
    /// window. Off preserves Catalog's normal headless behavior.
    /// </summary>
    public bool ShowBrowser
    {
        get => Volatile.Read(ref _showBrowser) != 0;
        set => Volatile.Write(ref _showBrowser, value ? 1 : 0);
    }

    public bool HasSession
    {
        get
        {
            _gate.Wait();
            try
            {
                return _session is not null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Ensures a browser session for one provider, opening the provider page so
    /// the secure session is negotiated before any API call. Reuses the existing
    /// session when the provider matches.
    /// </summary>
    public async Task<string> EnsureSessionAsync(
        string provider,
        string url,
        bool headless,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ThrowIfDisposed();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_session is not null
                && string.Equals(_sessionProvider, provider, StringComparison.Ordinal)
                && _sessionHeadless == headless
                && _sessionProxyMode == (_proxyPool?.IsProxyMode == true))
            {
                _lastUsedUtc = DateTimeOffset.UtcNow;
                return _session;
            }

            await CloseSessionCoreAsync(cancellationToken).ConfigureAwait(false);

            var response = await OpenWithProxyFailoverAsync(
                provider,
                url,
                headless,
                cancellationToken).ConfigureAwait(false);

            _session = response["session"]?.GetValue<string>()
                ?? throw new PyHostException("BAD_RESPONSE", "catalog.open tidak mengembalikan session");
            _sessionProvider = provider;
            _sessionHeadless = headless;
            _sessionProxyMode = _proxyPool?.IsProxyMode == true;
            _lastUsedUtc = DateTimeOffset.UtcNow;
            return _session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One API call through the page context, so provider signing and cookies
    /// apply. The response is bounded well under the protocol limit.
    /// </summary>
    public async Task<JsonObject> ApiAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return await SendOnSessionAsync(
            "catalog.api",
            payload => payload["url"] = url,
            payload => payload["timeout_ms"] = (int)ApiTimeout.TotalMilliseconds,
            ApiTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Browser-context fetch of one page into staging. The destination is
    /// validated here and again in Python, and only evidence comes back.
    /// </summary>
    public async Task<CatalogBrowserFetchEvidence> FetchToStagingAsync(
        string url,
        string relativePath,
        string? referer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var target = ResolveContained(relativePath);
        var response = await SendOnSessionAsync(
            "catalog.fetch",
            payload =>
            {
                payload["url"] = url;
                payload["path"] = target;
                payload["root"] = StagingRoot;
                payload["timeout_ms"] = (int)PageTimeout.TotalMilliseconds;
                if (!string.IsNullOrWhiteSpace(referer))
                {
                    payload["referer"] = referer;
                }
            },
            null,
            PageTimeout,
            cancellationToken).ConfigureAwait(false);

        return new CatalogBrowserFetchEvidence(
            response["status"]?.GetValue<int>() ?? 0,
            response["bytes"]?.GetValue<long>() ?? 0,
            response["sha256"]?.GetValue<string>(),
            response["content_type"]?.GetValue<string>() ?? string.Empty,
            response["path"]?.GetValue<string>());
    }

    /// <summary>
    /// Releases the browser without ending the host, so a later explicit action
    /// can start one again. Never called by a timer while work is in flight.
    /// </summary>
    public async Task ReleaseSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseSessionCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the Catalog-owned host immediately. The active request fails at
    /// once and a later explicit action creates a new host and browser session.
    /// This never targets CamoProf or another module's process.
    /// </summary>
    public void AbortSession()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var host = Interlocked.Exchange(ref _host, null);
        Interlocked.Exchange(ref _session, null);
        Interlocked.Exchange(ref _sessionProvider, null);
        _sessionHeadless = null;
        _sessionProxyMode = false;
        host?.Abort();
    }

    /// <summary>Canonicalizes a staging-relative path and refuses escapes.</summary>
    public string ResolveContained(string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(StagingRoot, relativePath));
        var root = Path.GetFullPath(StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "path unduhan keluar dari staging Catalog: " + relativePath);
        }

        return candidate;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PyHost? host;
        string? session;
        _gate.Wait();
        try
        {
            host = _host;
            session = _session;
            _host = null;
            _session = null;
            _sessionProvider = null;
            _sessionHeadless = null;
            _sessionProxyMode = false;
        }
        finally
        {
            _gate.Release();
        }

        if (host is null)
        {
            return;
        }

        // PyHost.Dispose walks its graceful shutdown ladder and can wait for a
        // browser to close. Keep that off the caller's thread, matching the
        // existing CamoProf composition; the ladder's last step kills the tree,
        // and only this feature's own process is ever targeted.
        _ = Task.Run(() =>
        {
            if (session is not null)
            {
                try
                {
                    host.SendAsync(
                        "catalog.close",
                        new JsonObject { ["session"] = session },
                        ApiTimeout,
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Dispose below is the backstop.
                }
            }

            host.Dispose();
        });

        _gate.Dispose();
    }

    private async Task<JsonObject> SendOnSessionAsync(
        string command,
        Action<JsonObject> configure,
        Action<JsonObject>? configureExtra,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_session is null)
            {
                throw new PyHostException(
                    "NO_BROWSER_SESSION",
                    "tidak ada session browser Catalog; buka provider lebih dulu");
            }

            var host = await EnsureHostCoreAsync().ConfigureAwait(false);
            var payload = new JsonObject { ["session"] = _session };
            configure(payload);
            configureExtra?.Invoke(payload);

            try
            {
                var response = await host
                    .SendAsync(command, payload, timeout, cancellationToken)
                    .ConfigureAwait(false);
                _lastUsedUtc = DateTimeOffset.UtcNow;
                return response;
            }
            catch (PyHostException ex) when (ex.Code is "BROWSER_GONE" or "HOST_EXITED" or "SESSION_NOT_FOUND")
            {
                // The browser is gone: drop the local registration so the next
                // explicit action bootstraps a fresh one.
                _session = null;
                _sessionProvider = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> OpenWithProxyFailoverAsync(
        string provider,
        string url,
        bool headless,
        CancellationToken cancellationToken)
    {
        var attempts = _proxyPool?.IsProxyMode == true ? MaxProxyOpenAttempts : 1;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = _proxyPool?.Acquire(ProxyTarget.Browser);
            var parameters = new JsonObject
            {
                ["provider"] = provider,
                ["url"] = url,
                ["headless"] = headless,
                ["timeout_ms"] = (int)BootstrapTimeout.TotalMilliseconds,
            };
            if (lease is not null) parameters["proxy"] = lease.ToLaunchOptions().ToJson();

            try
            {
                if (_openSessionOverride is not null)
                {
                    return await _openSessionOverride(parameters, cancellationToken).ConfigureAwait(false);
                }

                var host = await EnsureHostCoreAsync().ConfigureAwait(false);
                return await host
                    .SendAsync("catalog.open", parameters, BootstrapTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                lease is not null
                && IsRetryableProxyOpenFailure(ex, cancellationToken))
            {
                _proxyPool?.ReportFailure(lease);
                if (attempt >= attempts) throw;
            }
        }
    }

    private static bool IsRetryableProxyOpenFailure(
        Exception error,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        return error is TimeoutException
            || error is PyHostException pyhost
                && pyhost.Code is "BROWSER_LAUNCH" or "TIMEOUT";
    }

    private async Task CloseSessionCoreAsync(CancellationToken cancellationToken)
    {
        var host = _host;
        var session = _session;
        _session = null;
        _sessionProvider = null;
        _sessionHeadless = null;
        _sessionProxyMode = false;
        if (host is null || session is null)
        {
            return;
        }

        try
        {
            await host.SendAsync(
                "catalog.close",
                new JsonObject { ["session"] = session },
                ApiTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PyHostException)
        {
            // A vanished browser is already the desired end state.
        }
    }

    private async Task<PyHost> EnsureHostCoreAsync()
    {
        if (_host is not null)
        {
            return _host;
        }

        var python = RuntimeSetup.VenvPython;
        if (!File.Exists(python))
        {
            throw new InvalidOperationException(
                "runtime belum siap — jalankan Setup runtime di tab Runtime CamoProf");
        }

        var script = RuntimeSetup.DeployedPyhostScript;
        if (!File.Exists(script))
        {
            throw new InvalidOperationException("payload pyhost tidak ter-deploy: " + script);
        }

        // The host requires an absolute credential root to answer anything but
        // ping. Catalog never reads or writes Google profiles; it only
        // shares the resolved location so the host starts healthy.
        _host = PyHost.Start(python, script, CredenzPath.Resolve(), PluginName);
        await Task.CompletedTask.ConfigureAwait(false);
        return _host;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
