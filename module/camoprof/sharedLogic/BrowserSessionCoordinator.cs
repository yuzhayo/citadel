using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;

namespace Module.Camoprof.SharedLogic;

internal sealed class ProfileSessionChangedEventArgs : EventArgs
{
    public ProfileSessionChangedEventArgs(string profile, bool isOpen)
    {
        Profile = profile;
        IsOpen = isOpen;
    }

    public string Profile { get; }

    public bool IsOpen { get; }
}

/// <summary>
/// Owns CamoProf's single pyhost process and profile-to-session registry.
/// Browser mutations are serialized so launch/verify/delete cannot race.
/// </summary>
internal sealed class BrowserSessionCoordinator : IDisposable
{
    private readonly Dictionary<string, string> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _sync = new();
    private PyHost? _host;
    private int _disposed;

    public event EventHandler<ProfileSessionChangedEventArgs>? SessionChanged;

    public bool HasOpenSessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Count > 0;
            }
        }
    }

    public bool IsOpen(string profile)
    {
        lock (_sync)
        {
            return _sessions.ContainsKey(profile);
        }
    }

    public async Task OpenAsync(
        string profile,
        string? startUrl = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (IsOpen(profile))
            {
                throw new PyHostException("PROFILE_BUSY", "profile sudah punya session: " + profile);
            }

            var response = await EnsureHost().OpenSessionAsync(profile, startUrl, cancellationToken);
            var session = response["session"]?.GetValue<string>()
                ?? throw new PyHostException("BAD_RESPONSE", "session.open tidak mengembalikan session id");
            lock (_sync)
            {
                // Navigation may dispose the coordinator while pyhost is
                // answering. A disposed host already owns cleanup; never
                // resurrect its session in the local registry.
                ThrowIfDisposed();
                _sessions[profile] = session;
            }

            SessionChanged?.Invoke(this, new ProfileSessionChangedEventArgs(profile, true));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<JsonObject> VerifyGoogleAsync(
        string profile,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var session = GetSession(profile)
                ?? throw new PyHostException(
                    "SESSION_NOT_FOUND",
                    "tidak ada session terbuka untuk '" + profile + "'");
            try
            {
                return await EnsureHost().VerifySessionAsync(session, cancellationToken);
            }
            catch (PyHostException ex) when (ex.Code == "BROWSER_GONE")
            {
                Forget(profile);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <returns>True when a tracked session was closed or proven absent.</returns>
    public async Task<bool> CloseAsync(
        string profile,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var session = GetSession(profile);
            if (session is null)
            {
                return false;
            }

            try
            {
                await EnsureHost().CloseSessionAsync(session, cancellationToken);
            }
            catch (PyHostException ex) when (
                ex.Code is "SESSION_NOT_FOUND" or "BROWSER_GONE")
            {
                // Both outcomes prove no registered live session remains.
            }

            Forget(profile);
            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PyHost? host;
        lock (_sync)
        {
            _sessions.Clear();
            host = _host;
            _host = null;
        }

        // Do not wait for a possibly timed-out browser operation. Disposing
        // pyhost is the cancellation/backstop and closes its process tree.
        host?.Dispose();
    }

    private string? GetSession(string profile)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(profile, out var session) ? session : null;
        }
    }

    private void Forget(string profile)
    {
        var removed = false;
        lock (_sync)
        {
            removed = _sessions.Remove(profile);
        }

        if (removed)
        {
            SessionChanged?.Invoke(this, new ProfileSessionChangedEventArgs(profile, false));
        }
    }

    private PyHost EnsureHost()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_host is not null)
            {
                return _host;
            }

            var python = RuntimeSetup.VenvPython;
            if (!File.Exists(python))
            {
                throw new InvalidOperationException("runtime belum siap — buka tab Runtime lalu jalankan Setup runtime");
            }

            var script = RuntimeSetup.DeployedPyhostScript;
            if (!File.Exists(script))
            {
                throw new InvalidOperationException("payload pyhost tidak ter-deploy: " + script);
            }

            _host = PyHost.Start(python, script, CredenzPath.Resolve());
            return _host;
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
