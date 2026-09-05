using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;

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
/// lazily, held only while work needs it, released after bounded idle, and
/// never shares CamoProf's process, profile, or session registry.
///
/// Timeouts are fixed per operation class rather than negotiated per call.
/// </summary>
public sealed class DownloaderPyHostClient : IDisposable
{
    /// <summary>Camoufox bootstrap and first navigation.</summary>
    public static readonly TimeSpan BootstrapTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Catalog, detail, lookup, manifest, and browser fallback.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Each single page attempt.</summary>
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);

    /// <summary>An unused browser is released after this long.</summary>
    public static readonly TimeSpan IdleReleaseAfter = TimeSpan.FromSeconds(60);

    private const string PluginName = "mangareader_downloader";
    private static readonly TimeSpan IdleCheckPeriod = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private PyHost? _host;
    private string? _session;
    private string? _sessionProvider;
    private DateTimeOffset _lastUsedUtc = DateTimeOffset.UtcNow;
    private int _disposed;

    public DownloaderPyHostClient(string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        StagingRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(StagingRoot);
        _idleTimer = new Timer(OnIdleTick, null, IdleCheckPeriod, IdleCheckPeriod);
    }

    /// <summary>Absolute root every browser write must stay inside.</summary>
    public string StagingRoot { get; }

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
            if (_session is not null && string.Equals(_sessionProvider, provider, StringComparison.Ordinal))
            {
                _lastUsedUtc = DateTimeOffset.UtcNow;
                return _session;
            }

            await CloseSessionCoreAsync(cancellationToken).ConfigureAwait(false);

            var host = await EnsureHostCoreAsync().ConfigureAwait(false);
            var parameters = new JsonObject
            {
                ["provider"] = provider,
                ["url"] = url,
                ["headless"] = headless,
                ["timeout_ms"] = (int)BootstrapTimeout.TotalMilliseconds,
            };
            var response = await host
                .SendAsync("downloader.open", parameters, BootstrapTimeout, cancellationToken)
                .ConfigureAwait(false);

            _session = response["session"]?.GetValue<string>()
                ?? throw new PyHostException("BAD_RESPONSE", "downloader.open tidak mengembalikan session");
            _sessionProvider = provider;
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
            cancellationToken).ConfigureAwait(false);

        return new BrowserFetchEvidence(
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

        _idleTimer.Dispose();

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
                    "tidak ada session browser Downloader; buka provider lebih dulu");
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

    private async Task CloseSessionCoreAsync(CancellationToken cancellationToken)
    {
        var host = _host;
        var session = _session;
        _session = null;
        _sessionProvider = null;
        if (host is null || session is null)
        {
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
        // ping. The Downloader never reads or writes Google profiles; it only
        // shares the resolved location so the host starts healthy.
        _host = PyHost.Start(python, script, CredenzPath.Resolve(), PluginName);
        await Task.CompletedTask.ConfigureAwait(false);
        return _host;
    }

    private void OnIdleTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // A local idle check only: this never issues a remote request or a
        // health probe, so it cannot create background network traffic.
        if (DateTimeOffset.UtcNow - _lastUsedUtc < IdleReleaseAfter)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ReleaseSessionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Idle release is best effort; the next action re-bootstraps.
            }
        });
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
