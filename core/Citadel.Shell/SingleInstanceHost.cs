using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Citadel.Core;

namespace Citadel.Shell;

internal enum InstanceLaunchKind
{
    Owner,
    Ready,
    Starting,
    Refused,
    Failed,
}

internal sealed record InstanceLaunch(
    InstanceLaunchKind Kind,
    SingleInstanceHost? Owner = null,
    int OwnerProcessId = 0,
    nint WindowHandle = 0,
    string? Error = null)
{
    internal int ExitCode => Kind is InstanceLaunchKind.Owner
        or InstanceLaunchKind.Ready
        or InstanceLaunchKind.Starting
        ? 0
        : 1;
}

/// <summary>
/// Wins process ownership before WPF is initialized. A secondary process does
/// only one bounded named-pipe exchange and exits; the retained owner handles
/// every window operation on its Dispatcher.
/// </summary>
internal sealed class SingleInstanceHost : IDisposable
{
    private static readonly TimeSpan DefaultContactTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClientCommandTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _state = new();
    private readonly Task _listener;
    private Func<CancellationToken, Task<bool>>? _show;
    private nint _windowHandle;
    private bool _ready;
    private bool _pendingShow;
    private bool _stopping;
    private int _disposed;

    private SingleInstanceHost(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;

        // Create the first endpoint synchronously. Ownership is not reported as
        // healthy if the process cannot establish its activation channel.
        var firstServer = CreateServer();
        _listener = ListenAsync(firstServer);
    }

    internal static InstanceLaunch Start(
        IReadOnlyCollection<string> args,
        string executablePath,
        TimeSpan? contactTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string identity;
        try
        {
            identity = Identity(executablePath, Process.GetCurrentProcess().SessionId);
        }
        catch (Exception exception)
        {
            return Failed($"Citadel could not derive its owner identity: {exception.Message}");
        }
        var mutexName = $@"Local\Citadel.{identity}.Owner";
        var pipeName = $"Citadel.{identity}.Activation";

        Mutex mutex;
        bool created;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out created);
        }
        catch (Exception exception)
        {
            return Failed($"Citadel could not create its owner gate: {exception.Message}");
        }

        if (created) return CreateOwner(mutex, pipeName);

        if (args.Any(arg => string.Equals(
                arg,
                "--reset-ui",
                StringComparison.OrdinalIgnoreCase)))
        {
            mutex.Dispose();
            return new InstanceLaunch(
                InstanceLaunchKind.Refused,
                Error: "Citadel is already running. Exit Citadel before --reset-ui.");
        }

        var response = NotifyOwner(pipeName, contactTimeout ?? DefaultContactTimeout);
        if (response.Kind is InstanceLaunchKind.Ready or InstanceLaunchKind.Starting)
        {
            mutex.Dispose();
            return response;
        }

        if (TryTakeOwnership(mutex)) return CreateOwner(mutex, pipeName);

        mutex.Dispose();
        return response;
    }

    internal void MarkReady(
        nint windowHandle,
        Func<CancellationToken, Task<bool>> show)
    {
        if (windowHandle == 0) throw new ArgumentOutOfRangeException(nameof(windowHandle));
        ArgumentNullException.ThrowIfNull(show);

        Func<CancellationToken, Task<bool>>? pending = null;
        lock (_state)
        {
            if (_stopping) return;
            _windowHandle = windowHandle;
            _show = show;
            _ready = true;
            if (_pendingShow)
            {
                _pendingShow = false;
                pending = show;
            }
        }

        if (pending is not null) _ = InvokeShowAsync(pending, _stop.Token);
    }

    internal void StopListening()
    {
        lock (_state)
        {
            if (_stopping) return;
            _stopping = true;
            _ready = false;
            _show = null;
        }

        _stop.Cancel();
        try
        {
            if (!_listener.Wait(TimeSpan.FromSeconds(1)))
            {
                Log.Main("[Startup] activation listener did not stop within one second");
            }
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        StopListening();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process no longer owns it; closing the handle is still safe.
        }
        _mutex.Dispose();
        _stop.Dispose();
    }

    private static InstanceLaunch CreateOwner(Mutex mutex, string pipeName)
    {
        try
        {
            return new InstanceLaunch(
                InstanceLaunchKind.Owner,
                Owner: new SingleInstanceHost(mutex, pipeName));
        }
        catch (Exception exception)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            mutex.Dispose();
            return Failed($"Citadel could not start its activation channel: {exception.Message}");
        }
    }

    private static InstanceLaunch NotifyOwner(string pipeName, TimeSpan timeout)
    {
        var timeoutMilliseconds = Math.Clamp(
            (int)Math.Ceiling(timeout.TotalMilliseconds),
            1,
            int.MaxValue);
        var elapsed = Stopwatch.StartNew();

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            client.Connect(timeoutMilliseconds);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine("SHOW");

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            using var responseTimeout = new CancellationTokenSource(remaining);
            var response = reader
                .ReadLineAsync(responseTimeout.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            return ParseResponse(response);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or OperationCanceledException)
        {
            return Failed($"Citadel is already running but did not answer: {exception.Message}");
        }
    }

    private static InstanceLaunch ParseResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return Failed("Citadel's running owner returned an empty activation response.");
        }

        var fields = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 3
            && string.Equals(fields[0], "READY", StringComparison.Ordinal)
            && int.TryParse(fields[1], out var processId)
            && long.TryParse(fields[2], out var handleValue)
            && processId > 0
            && handleValue != 0)
        {
            var windowHandle = new nint(handleValue);
            NativeWindowActivation.TryActivate(processId, windowHandle);
            return new InstanceLaunch(
                InstanceLaunchKind.Ready,
                OwnerProcessId: processId,
                WindowHandle: windowHandle);
        }

        if (fields.Length == 2
            && string.Equals(fields[0], "STARTING", StringComparison.Ordinal)
            && int.TryParse(fields[1], out processId)
            && processId > 0)
        {
            return new InstanceLaunch(
                InstanceLaunchKind.Starting,
                OwnerProcessId: processId);
        }

        if (fields.Length >= 2
            && string.Equals(fields[0], "ERROR", StringComparison.Ordinal))
        {
            return Failed(
                $"Citadel's running owner could not activate the window: {string.Join(' ', fields.Skip(1))}");
        }

        return Failed($"Citadel's running owner returned an invalid response: {response}");
    }

    private static bool TryTakeOwnership(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private async Task ListenAsync(NamedPipeServerStream firstServer)
    {
        NamedPipeServerStream? server = firstServer;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var current = server ?? CreateServer();
                server = null;
                using (current)
                {
                    try
                    {
                        await current.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                        using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        commandTimeout.CancelAfter(ClientCommandTimeout);
                        await HandleConnectionAsync(current, commandTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Main("[Startup] activation connection timed out");
                    }
                    catch (IOException exception)
                    {
                        Log.Main($"[Startup] activation connection dropped: {exception.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Main($"[Startup] activation listener failed: {exception.Message}");
        }
        finally
        {
            server?.Dispose();
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var response = string.Equals(command, "SHOW", StringComparison.Ordinal)
            ? await PrepareShowResponseAsync(cancellationToken).ConfigureAwait(false)
            : "ERROR unsupported-command";
        await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PrepareShowResponseAsync(CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<bool>>? show;
        nint windowHandle;
        lock (_state)
        {
            if (_stopping) return "ERROR owner-exiting";

            if (_ready)
            {
                show = _show;
                windowHandle = _windowHandle;
            }
            else
            {
                _pendingShow = true;
                return $"STARTING {Environment.ProcessId}";
            }
        }

        if (show is null || !await InvokeShowAsync(show, cancellationToken).ConfigureAwait(false))
        {
            lock (_state)
            {
                return _stopping
                    ? "ERROR owner-exiting"
                    : "ERROR activation-failed";
            }
        }

        lock (_state)
        {
            return _stopping || !_ready || _windowHandle != windowHandle
                ? "ERROR owner-exiting"
                : $"READY {Environment.ProcessId} {windowHandle}";
        }
    }

    private static async Task<bool> InvokeShowAsync(
        Func<CancellationToken, Task<bool>> show,
        CancellationToken cancellationToken)
    {
        try
        {
            return await show(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            Log.Main($"[Startup] activation dispatch failed: {exception.Message}");
            return false;
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    internal static string Identity(string executablePath, int sessionId)
    {
        if (sessionId < 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
        var normalizedPath = Path
            .GetFullPath(executablePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{sid}\n{sessionId}\n{normalizedPath}"));
        return Convert.ToHexString(bytes.AsSpan(0, 16));
    }

    private static InstanceLaunch Failed(string message) =>
        new(InstanceLaunchKind.Failed, Error: message);
}

internal static class NativeWindowActivation
{
    internal static bool TryActivate(int processId, nint windowHandle)
    {
        if (processId <= 0 || windowHandle == 0 || !IsWindow(windowHandle)) return false;
        GetWindowThreadProcessId(windowHandle, out var actualProcessId);
        if (actualProcessId != (uint)processId) return false;

        var activated = SetForegroundWindow(windowHandle);
        SetFocus(windowHandle);
        return activated;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint windowHandle);
}
