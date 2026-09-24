using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Citadel.Core;

namespace Citadel.Shell;

/// <summary>
/// Gives one completed MainWindow a resident lifetime. Hide/Show never tears
/// down its Router, view lifetime, token subscriptions, or theme resources.
///
/// Exit: tray Exit awaits the Shell's single <c>RequestShutdownAsync</c>
/// (StopAll → latch → Application.Shutdown). <see cref="ExitRequested"/> is
/// set only after that succeeds, so a failed Stop leaves Exit retryable.
/// Windows session ending keeps the sync latch path — no false async drain
/// guarantee when the OS is already tearing the session down.
/// </summary>
internal sealed class ResidentShell : IDisposable
{
    private readonly MainWindow _window;
    private readonly Action _closeSettingsWindow;
    private readonly ITrayHost? _tray;
    private readonly Action _stopInstance;
    private readonly Func<Task> _requestShutdown;
    private bool _exitRequested;
    private bool _exitInProgress;
    private bool _infrastructureStopped;
    private bool _disposed;

    internal ResidentShell(
        MainWindow window,
        Action closeSettingsWindow,
        ITrayHost? tray,
        Action stopInstance,
        Func<Task> requestShutdown)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _closeSettingsWindow = closeSettingsWindow
            ?? throw new ArgumentNullException(nameof(closeSettingsWindow));
        _tray = tray;
        _stopInstance = stopInstance ?? throw new ArgumentNullException(nameof(stopInstance));
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));

        if (_tray is null) return;

        _window.Closing += OnWindowClosing;
        _tray.OpenRequested += OnOpenRequested;
        _tray.ExitRequested += OnExitRequested;
    }

    internal bool ResidentEnabled => _tray is not null;

    /// <summary>True only after a successful exit (StopAll + latch).</summary>
    internal bool ExitRequested => _exitRequested;

    /// <summary>True while an async Exit is awaiting StopAll — blocks Open.</summary>
    internal bool ExitInProgress => _exitInProgress;

    internal void RequestOpen() => _ = RequestOpenAsync(CancellationToken.None);

    internal async Task<bool> RequestOpenAsync(CancellationToken cancellationToken)
    {
        if (_disposed
            || _exitRequested
            || _exitInProgress
            || _window.Dispatcher.HasShutdownStarted
            || _window.Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        if (_window.Dispatcher.CheckAccess()) return OpenWindow();

        try
        {
            var operation = _window.Dispatcher.InvokeAsync(
                OpenWindow,
                DispatcherPriority.Normal,
                cancellationToken);
            return await operation.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidOperationException) when (
            _window.Dispatcher.HasShutdownStarted
            || _window.Dispatcher.HasShutdownFinished)
        {
            return false;
        }
        catch (Exception exception)
        {
            Log.Main($"[Startup] window activation failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Test seam: awaitable tray Exit. Production goes through the tray event
    /// (fire-and-forget onto this).
    /// </summary>
    internal Task RequestExitAsync() => BeginExitAsync();

    /// <summary>
    /// Windows session ending: latch immediately without StopAll. The OS is
    /// already tearing the session down — async drain is not guaranteed here;
    /// durable module work recovers on next Start.
    /// </summary>
    internal void PrepareForSessionEnd()
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            if (_window.Dispatcher.HasShutdownStarted
                || _window.Dispatcher.HasShutdownFinished)
            {
                return;
            }
            _window.Dispatcher.BeginInvoke(new Action(PrepareForSessionEnd));
            return;
        }

        CompleteExit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exitRequested = true;

        if (_tray is not null)
        {
            _window.Closing -= OnWindowClosing;
            _tray.OpenRequested -= OnOpenRequested;
            _tray.ExitRequested -= OnExitRequested;
        }
        CloseSettingsWindow();
        StopInfrastructure();
    }

    /// <summary>
    /// Latch a successful exit: mark ExitRequested, close Settings, stop tray
    /// and single-instance infrastructure. Called by App after StopAll succeeds
    /// (and by session end / Dispose).
    /// </summary>
    internal void CompleteExit()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _exitInProgress = true;
        CloseSettingsWindow();
        StopInfrastructure();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs args)
    {
        if (_exitRequested || _tray is null) return;

        args.Cancel = true;
        CloseSettingsWindow();
        _window.Hide();
        Log.Main("[Startup] main window hidden; owner remains resident");
    }

    private void OnOpenRequested() => RequestOpen();

    private void OnExitRequested() => _ = BeginExitAsync();

    private async Task BeginExitAsync()
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            if (_window.Dispatcher.HasShutdownStarted
                || _window.Dispatcher.HasShutdownFinished)
            {
                return;
            }
            await _window.Dispatcher.InvokeAsync(BeginExitAsync);
            return;
        }

        // Duplicate Exit while StopAll is in flight, or after success: ignore.
        if (_disposed || _exitRequested || _exitInProgress) return;

        _exitInProgress = true;
        try
        {
            // StopAll → (on success) CompleteExit + Application.Shutdown.
            // On failure App returns without latching — Exit stays retryable.
            await _requestShutdown().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Main($"[Startup] exit failed: {exception.GetBaseException().Message}");
        }
        finally
        {
            if (!_exitRequested)
            {
                // Failed Stop: clear in-progress so Exit/Open work again.
                _exitInProgress = false;
            }
        }
    }

    private bool OpenWindow()
    {
        if (_disposed
            || _exitRequested
            || _exitInProgress
            || _window.Dispatcher.HasShutdownStarted
            || _window.Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        if (!_window.IsVisible) _window.Show();
        _window.Activate();
        _window.Focus();
        Log.Main("[Startup] existing window activated");
        return _window.IsVisible;
    }

    private void CloseSettingsWindow()
    {
        try
        {
            _closeSettingsWindow();
        }
        catch (Exception exception)
        {
            Log.Main($"[Startup] settings window close failed: {exception.Message}");
        }
    }

    private void StopInfrastructure()
    {
        if (_infrastructureStopped) return;
        _infrastructureStopped = true;

        try
        {
            _tray?.Dispose();
        }
        catch (Exception exception)
        {
            Log.Main($"[Startup] tray shutdown failed: {exception.Message}");
        }

        try
        {
            _stopInstance();
        }
        catch (Exception exception)
        {
            Log.Main($"[Startup] activation shutdown failed: {exception.Message}");
        }
    }
}
