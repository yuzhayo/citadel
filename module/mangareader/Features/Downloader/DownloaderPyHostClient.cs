using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// Result of one browser-context fetch that wrote straight into staging. Page
/// bytes never travel through the pyhost protocol: only this evidence does.
/// </summary>
public sealed record BrowserFetchEvidence(
    int Status,
    long Bytes,
    string? Sha256,
    string ContentType,
    string? Path);

/// <summary>
/// The Downloader's own command/payload adapter over the shared pyhost
/// transport. It owns this feature's browser lifetime: the host is started
/// lazily and remains available across navigation and close-to-tray. It ends
/// only through the feature's explicit Stop command or application shutdown,
/// and never shares CamoProf's process, profile, or session registry.
///
/// Timeouts are fixed per operation class rather than negotiated per call.
/// </summary>
public sealed class DownloaderPyHostClient : IDisposable
{
    /// <summary>Camoufox bootstrap and first navigation.</summary>
    public static readonly TimeSpan BootstrapTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Maximum initial navigation wait for one proxied browser attempt.</summary>
    public static readonly TimeSpan ProxyConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Catalog, detail, lookup, manifest, and browser fallback.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Each single page attempt.</summary>
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);

    private const string PluginName = "mangareader_downloader";
    private const int MaxProxyOpenAttempts = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProxyPoolAdapter? _proxyPool;
    private readonly string? _profileIdentity;
    private readonly ProxyLease? _fixedLease;
    private ProxyReservation? _sessionReservation;
    private readonly AsyncLocal<bool> _borrowOnly = new();
    private readonly AsyncLocal<string?> _borrowedSession = new();
    private readonly AsyncLocal<long> _borrowedGeneration = new();
    private long _sessionGeneration;
    private readonly Func<JsonObject, CancellationToken, Task<JsonObject>>? _openSessionOverride;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonObject>>? _commandOverride;
    private PyHost? _host;
    private string? _session;
    private string? _sessionProvider;
    private bool? _sessionHeadless;
    private bool _sessionProxyMode;
    private int _showBrowser;
    private int _disposed;

    public DownloaderPyHostClient(string stagingRoot) : this(stagingRoot, null)
    {
    }

    internal DownloaderPyHostClient(string stagingRoot, ProxyPoolAdapter? proxyPool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        StagingRoot = Path.GetFullPath(stagingRoot);
        _proxyPool = proxyPool;
        Directory.CreateDirectory(StagingRoot);
    }

    internal DownloaderPyHostClient(string stagingRoot, ProxyPoolAdapter pool,
        string profileIdentity, ProxyLease fixedLease,
        Func<JsonObject, CancellationToken, Task<JsonObject>>? openSession = null,
        Func<string, JsonObject, CancellationToken, Task<JsonObject>>? command = null) : this(stagingRoot, pool)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(profileIdentity, @"^queue-[a-f0-9]{32}$"))
            throw new ArgumentException("Invalid Queue profile identity.", nameof(profileIdentity));
        _profileIdentity = profileIdentity;
        _fixedLease = fixedLease;
        _openSessionOverride = openSession;
        _commandOverride = command;
    }

    internal bool HasSessionFor(string provider, bool proxyMode) =>
        HasSessionFor(provider) && _sessionProxyMode == proxyMode;

    // Called only inside QueueSharedSessionAdapter's serial gate. Ensuring the
    // borrowed source may reuse this session, but can never bootstrap or replace it.
    internal IDisposable BorrowExistingSession()
    {
        _borrowOnly.Value = true;
        _borrowedSession.Value = _session;
        _borrowedGeneration.Value = Volatile.Read(ref _sessionGeneration);
        return new BorrowScope(this);
    }

    private sealed class BorrowScope(DownloaderPyHostClient owner) : IDisposable
    {
        public void Dispose()
        {
            owner._borrowOnly.Value = false;
            owner._borrowedSession.Value = null;
        }
    }

    internal DownloaderPyHostClient(
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
    /// window. Off preserves the Downloader's normal headless behavior.
    /// </summary>
    public bool ShowBrowser
    {
        get => Volatile.Read(ref _showBrowser) != 0;
        set => Volatile.Write(ref _showBrowser, value ? 1 : 0);
    }

    /// <summary>
    /// Non-blocking presentation snapshot. A bootstrap owns <see cref="_gate"/>
    /// for as long as 120 seconds, so UI code must never wait on that operation
    /// merely to decide whether the Stop button is enabled.
    /// </summary>
    public bool HasSession => Volatile.Read(ref _session) is not null;

    /// <summary>
    /// Non-blocking provider-specific readiness snapshot used by Queue. This
    /// never creates a host or waits for an in-flight bootstrap.
    /// </summary>
    public bool HasSessionFor(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return Volatile.Read(ref _session) is not null
            && string.Equals(
                Volatile.Read(ref _sessionProvider),
                provider,
                StringComparison.Ordinal);
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
            if (_borrowOnly.Value)
            {
                if (!HasSessionFor(provider) || _session != _borrowedSession.Value
                    || _sessionGeneration != _borrowedGeneration.Value)
                    throw new PyHostException("NO_BROWSER_SESSION", "Shared session is no longer available.");
                return _session!;
            }
            if (_session is not null
                && string.Equals(_sessionProvider, provider, StringComparison.Ordinal)
                && _sessionHeadless == headless
                && _sessionProxyMode == (_fixedLease is not null || _proxyPool?.IsProxyMode == true))
            {
                return _session;
            }

            await CloseSessionCoreAsync(cancellationToken).ConfigureAwait(false);

            var response = await OpenWithProxyFailoverAsync(
                provider,
                url,
                headless,
                cancellationToken).ConfigureAwait(false);

            _session = response["session"]?.GetValue<string>()
                ?? throw new PyHostException("BAD_RESPONSE", "downloader.open tidak mengembalikan session");
            _sessionProvider = provider;
            _sessionHeadless = headless;
            _sessionProxyMode = _fixedLease is not null || _proxyPool?.IsProxyMode == true;
            Interlocked.Increment(ref _sessionGeneration);
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
            "downloader.api",
            payload => payload["url"] = url,
            payload => payload["timeout_ms"] = (int)ApiTimeout.TotalMilliseconds,
            ApiTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Browser-context fetch of one page into staging. The destination is
    /// validated here and again in Python, and only evidence comes back.
    /// </summary>
    public async Task<BrowserFetchEvidence> FetchToStagingAsync(
        string url,
        string relativePath,
        string? referer,
        bool requiredProxyMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var target = ResolveContained(relativePath);
        var response = await SendOnSessionAsync(
            "downloader.fetch",
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
            cancellationToken,
            requiredProxyMode).ConfigureAwait(false);

        return new BrowserFetchEvidence(
            response["status"]?.GetValue<int>() ?? 0,
            response["bytes"]?.GetValue<long>() ?? 0,
            response["sha256"]?.GetValue<string>(),
            response["content_type"]?.GetValue<string>() ?? string.Empty,
            response["path"]?.GetValue<string>());
    }

    /// <summary>
    /// Releases the browser without ending the host, so a failed provider
    /// handshake can retry cleanly. It is never triggered by inactivity or
    /// routed-view navigation.
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
    /// Stops the Downloader-owned host immediately. The active request fails at
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
        Interlocked.Exchange(ref _sessionReservation, null)?.Dispose();
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
                "path unduhan keluar dari staging Downloader: " + relativePath);
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
                        "downloader.close",
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
            Interlocked.Exchange(ref _sessionReservation, null)?.Dispose();
        });

        _gate.Dispose();
    }

    private async Task<JsonObject> SendOnSessionAsync(
        string command,
        Action<JsonObject> configure,
        Action<JsonObject>? configureExtra,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool? requiredProxyMode = null)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_session is null || (_borrowOnly.Value && (_session != _borrowedSession.Value
                || _sessionGeneration != _borrowedGeneration.Value)))
            {
                throw new PyHostException(
                    "NO_BROWSER_SESSION",
                    "tidak ada session browser Downloader; buka provider lebih dulu");
            }
            if (requiredProxyMode is { } expected && _sessionProxyMode != expected)
            {
                throw new PyHostException(
                    "SESSION_TRANSPORT_MISMATCH",
                    "session browser tidak memakai mode proxy/direct yang dikunci untuk chapter ini");
            }

            var payload = new JsonObject { ["session"] = _session };
            configure(payload);
            configureExtra?.Invoke(payload);
            if (_commandOverride is not null)
                return await _commandOverride(command, payload, cancellationToken).ConfigureAwait(false);
            var host = await EnsureHostCoreAsync().ConfigureAwait(false);

            try
            {
                var response = await host
                    .SendAsync(command, payload, timeout, cancellationToken)
                    .ConfigureAwait(false);
                return response;
            }
            catch (PyHostException ex) when (_borrowOnly.Value && ex.Code == "TIMEOUT")
            {
                // The protocol owner dispatches serially. A successful ping is
                // a settlement barrier for the previous timed-out command. Do
                // not release the borrowed gate/staging merely because C# timed out.
                while (true)
                {
                    try { await host.PingAsync(CancellationToken.None).ConfigureAwait(false); break; }
                    catch (PyHostException pending) when (pending.Code == "TIMEOUT") { }
                    catch (Exception) { break; } // stopped/exited host can no longer write
                }
                throw;
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
        var attempts = _fixedLease is not null ? 1 : _proxyPool?.IsProxyMode == true ? MaxProxyOpenAttempts : 1;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = _fixedLease ?? _proxyPool?.Acquire(ProxyTarget.Browser);
            if (lease is not null && _fixedLease is null)
                _sessionReservation = await _proxyPool!.Reservations.ReserveAsync(
                    "downloader-browser", [lease.Endpoint], cancellationToken).ConfigureAwait(false);
            var parameters = new JsonObject
            {
                ["provider"] = provider,
                ["url"] = url,
                ["headless"] = headless,
                ["timeout_ms"] = (int)BootstrapTimeout.TotalMilliseconds,
            };
            if (lease is not null)
            {
                parameters["proxy"] = lease.ToLaunchOptions().ToJson();
                parameters["connect_timeout_ms"] = (int)ProxyConnectTimeout.TotalMilliseconds;
            }
            if (_profileIdentity is not null) parameters["profile_identity"] = _profileIdentity;

            try
            {
                if (_openSessionOverride is not null)
                {
                    return await _openSessionOverride(parameters, cancellationToken).ConfigureAwait(false);
                }

                var host = await EnsureHostCoreAsync().ConfigureAwait(false);
                return await host
                    .SendAsync("downloader.open", parameters, BootstrapTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                lease is not null
                && IsRetryableProxyOpenFailure(ex, cancellationToken))
            {
                _proxyPool?.ReportFailure(lease);
                Interlocked.Exchange(ref _sessionReservation, null)?.Dispose();
                if (attempt >= attempts) throw;
            }
            catch
            {
                Interlocked.Exchange(ref _sessionReservation, null)?.Dispose();
                throw;
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
            Interlocked.Exchange(ref _sessionReservation, null)?.Dispose();
            return;
        }

        try
        {
            await host.SendAsync(
                "downloader.close",
                new JsonObject { ["session"] = session },
                ApiTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed close must settle the owned host before releasing its proxy.
            host.Abort();
            _host = null;
            throw;
        }
        finally { Interlocked.Exchange(ref _sessionReservation, null)?.Dispose(); }
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
        // ping. The Downloader never reads or writes Google profiles; it only
        // shares the resolved location so the host starts healthy.
        _host = PyHost.Start(python, script, CredenzPath.Resolve(), PluginName);
        await Task.CompletedTask.ConfigureAwait(false);
        return _host;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
