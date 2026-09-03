using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CitadelBridge;

/// <summary>
/// A structured pyhost failure: the wire-level "error.code" from the
/// protocol, so callers can branch on PROFILE_BUSY, BROWSER_GONE, etc.
/// </summary>
public sealed class PyHostException : Exception
{
    public PyHostException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>
/// Client for module/sharedLogic/pyhost — the only C#↔Python seam.
/// Owns one python process; NDJSON over stdin/stdout; exactly one response
/// per request id. Dispose shuts the host down gracefully (shutdown command
/// → stdin EOF) and only escalates to killing the process tree when the
/// grace period expires — the orphan guard lives on BOTH sides.
///
/// This file is shared SOURCE (module/sharedLogic/cs), compiled into each
/// citizen. It must stay free of WPF and of any Citadel contract types.
/// </summary>
public sealed class PyHost : IDisposable
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _nextId;
    private int _disposeStarted;
    private int _disposed;

    private PyHost(Process process)
    {
        _process = process;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(CopyStderrAsync);
    }

    /// <summary>pyhost's stderr, line by line — diagnostics only.</summary>
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Spawn a host. All three paths must be absolute.
    /// <paramref name="pyhostPlugins"/> is an optional comma-separated
    /// list of feature-plugin package names to activate via
    /// CITADEL_PYHOST_PLUGINS (e.g. "camoprof_add_profile"); the shared
    /// core stays feature-free and loads plugins by name only.
    /// </summary>
    public static PyHost Start(
        string pythonExe,
        string pyhostScript,
        string credenzDir,
        string? pyhostPlugins = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-u"); // unbuffered: NDJSON must flush per line
        startInfo.ArgumentList.Add(pyhostScript);
        startInfo.Environment["CITADEL_CREDENZ"] = credenzDir;
        if (!string.IsNullOrWhiteSpace(pyhostPlugins))
        {
            startInfo.Environment["CITADEL_PYHOST_PLUGINS"] = pyhostPlugins;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new PyHostException("SPAWN_FAILED", "python process failed to start: " + pythonExe);
        }

        return new PyHost(process);
    }

    public async Task<JsonObject> PingAsync(CancellationToken cancellationToken = default)
        => await SendAsync("ping", null, DefaultTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<JsonObject> OpenSessionAsync(
        string profile,
        string? startUrl,
        bool headless = false,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["profile"] = profile,
            ["headless"] = headless,
        };
        if (!string.IsNullOrWhiteSpace(startUrl))
        {
            parameters["start_url"] = startUrl;
        }

        return await SendAsync("session.open", parameters, DefaultTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JsonObject> InspectGoogleAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => await SendAsync(
                "google.inspect",
                new JsonObject { ["session"] = sessionId },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonObject> NavigateSessionAsync(
        string sessionId,
        string url,
        CancellationToken cancellationToken = default)
        => await SendAsync(
                "session.navigate",
                new JsonObject
                {
                    ["session"] = sessionId,
                    ["url"] = url,
                },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonObject> RelogGoogleAsync(
        string sessionId,
        string email,
        string password,
        CancellationToken cancellationToken = default)
        => await SendAsync(
                "google.relogin",
                new JsonObject
                {
                    ["session"] = sessionId,
                    ["email"] = email,
                    ["password"] = password,
                },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonObject> VerifySessionAsync(
        string sessionId, CancellationToken cancellationToken = default)
        => await SendAsync(
                "session.verify",
                new JsonObject { ["session"] = sessionId },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Enrollment commands (protocol v1). Start arms the capture listener
    /// BEFORE navigating the enrollment page to Google's sign-in, so the
    /// response returning means capture is live. Status never carries
    /// plaintext. Finish is the ONLY command whose response contains a
    /// secret, exactly once — the caller must treat it as confidential and
    /// is responsible for not forwarding it to UI layers. Cancel is an
    /// idempotent full teardown.
    /// </summary>
    public async Task<JsonObject> StartGoogleEnrollmentAsync(
        string sessionId,
        string? expectedEmail = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject { ["session"] = sessionId };
        if (!string.IsNullOrWhiteSpace(expectedEmail))
        {
            parameters["expected_email"] = expectedEmail;
        }

        return await SendAsync(
                "google.enrollment.start",
                parameters,
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JsonObject> GoogleEnrollmentStatusAsync(
        string sessionId, CancellationToken cancellationToken = default)
        => await SendAsync(
                "google.enrollment.status",
                new JsonObject { ["session"] = sessionId },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonObject> FinishGoogleEnrollmentAsync(
        string sessionId, CancellationToken cancellationToken = default)
        => await SendAsync(
                "google.enrollment.finish",
                new JsonObject { ["session"] = sessionId },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonObject> CancelGoogleEnrollmentAsync(
        string sessionId, CancellationToken cancellationToken = default)
        => await SendAsync(
                "google.enrollment.cancel",
                new JsonObject { ["session"] = sessionId },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonObject> CloseSessionAsync(
        string sessionId, CancellationToken cancellationToken = default)
        => await SendAsync(
                "session.close",
                new JsonObject { ["session"] = sessionId },
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonObject> SendAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => await SendCoreAsync(
                command,
                parameters,
                timeout,
                cancellationToken,
                allowDuringDispose: false)
            .ConfigureAwait(false);

    private async Task<JsonObject> SendCoreAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowDuringDispose)
    {
        if (allowDuringDispose)
        {
            ThrowIfDisposed();
        }
        else
        {
            ThrowIfDisposing();
        }

        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject { ["id"] = id, ["cmd"] = command };
        if (parameters is not null)
        {
            foreach (var pair in parameters)
            {
                request[pair.Key] = pair.Value?.DeepClone();
            }
        }

        if (timeout != DefaultTimeout)
        {
            request["timeout"] = timeout.TotalSeconds;
        }

        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new PyHostException("INTERNAL", "duplicate request id " + id);
        }

        var ownsWriteGate = false;
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsWriteGate = true;

            // Dispose may start while an external request is waiting for the
            // writer. Re-check under the gate so nothing can be written after
            // the internal shutdown request has begun.
            if (allowDuringDispose)
            {
                ThrowIfDisposed();
            }
            else
            {
                ThrowIfDisposing();
            }

            await _process.StandardInput.WriteLineAsync(
                request.ToJsonString()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new PyHostException("WRITE_FAILED", ex.Message);
        }
        finally
        {
            if (ownsWriteGate)
            {
                _writeGate.Release();
            }
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var registration = timeoutSource.Token.Register(
            () => completion.TrySetException(
                new PyHostException("TIMEOUT", command + " exceeded " + timeout.TotalSeconds + "s")))
            .ConfigureAwait(false);

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            // A timed-out/cancelled request may never receive a late response.
            // Do not retain its completion for the lifetime of the host.
            _pending.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            // Graceful ladder: shutdown command → stdin EOF → kill the tree.
            // The shutdown request goes out BEFORE _disposed is raised —
            // marking first made SendAsync reject it and the command was
            // never sent (codex audit finding #2); only EOF saved us.
            if (!_process.HasExited)
            {
                try
                {
                    SendCoreAsync(
                            "shutdown",
                            null,
                            TimeSpan.FromSeconds(5),
                            CancellationToken.None,
                            allowDuringDispose: true)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception)
                {
                    // Host may already be gone; the ladder continues regardless.
                }

                try
                {
                    _process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // Pipe already broken — EOF already delivered.
                }

                if (!_process.WaitForExit((int)GracePeriod.TotalMilliseconds))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception)
        {
            // Dispose must never throw; the shell's Lifetime runs it last.
        }
        finally
        {
            Volatile.Write(ref _disposed, 1);
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var completion))
                {
                    completion.TrySetException(new PyHostException("HOST_DISPOSED", "pyhost disposed"));
                }
            }

            _process.Dispose();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? endedBy = null;
        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break; // EOF — the host exited or closed its end.
                }

                JsonObject? message = null;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    Diagnostic?.Invoke("non-JSON stdout line (protocol bug): " + line);
                    continue;
                }

                if (message is null || message.ContainsKey("event"))
                {
                    continue; // v2 events are reserved; v1 ignores them.
                }

                if (message["id"] is not JsonValue idValue
                    || !idValue.TryGetValue<long>(out var id))
                {
                    continue;
                }

                if (!_pending.TryRemove(id, out var completion))
                {
                    continue;
                }

                if (message["ok"]?.GetValue<bool>() == true)
                {
                    completion.TrySetResult(message);
                }
                else
                {
                    var error = message["error"] as JsonObject;
                    completion.TrySetException(new PyHostException(
                        error?["code"]?.GetValue<string>() ?? "UNKNOWN",
                        error?["message"]?.GetValue<string>() ?? "unknown pyhost error"));
                }
            }
        }
        catch (Exception ex)
        {
            endedBy = ex;
        }

        // The read loop is the truth about the process being gone: every
        // outstanding request fails at once instead of hanging to timeout.
        var gone = new PyHostException(
            "HOST_EXITED",
            endedBy?.Message ?? "pyhost closed stdout (EOF)");
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(gone);
            }
        }
    }

    private async Task CopyStderrAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                Diagnostic?.Invoke(line);
            }
        }
        catch (Exception)
        {
            // stderr is diagnostics; a broken pipe there changes nothing.
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ThrowIfDisposing()
        => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0 || Volatile.Read(ref _disposed) != 0,
            this);
}
