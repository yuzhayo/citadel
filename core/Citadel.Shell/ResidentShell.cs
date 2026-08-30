using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Citadel.Core;

namespace Citadel.Shell;

/// <summary>
/// Gives one completed MainWindow a resident lifetime. Hide/Show never tears
/// down its Router, view lifetime, token subscriptions, or theme resources.
/// </summary>
internal sealed class ResidentShell : IDisposable
{
    private readonly MainWindow _window;
    private readonly Action _closeSettingsWindow;
    private readonly ITrayHost? _tray;
    private readonly Action _stopInstance;
    private readonly Action _shutdown;
    private bool _exitRequested;
    private bool _infrastructureStopped;
    private bool _disposed;

    internal ResidentShell(
        MainWindow window,
        Action closeSettingsWindow,
        ITrayHost? tray,
        Action stopInstance,
        Action shutdown)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _closeSettingsWindow = closeSettingsWindow
            ?? throw new ArgumentNullException(nameof(closeSettingsWindow));
        _tray = tray;
        _stopInstance = stopInstance ?? throw new ArgumentNullException(nameof(stopInstance));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));

        if (_tray is null) return;

        _window.Closing += OnWindowClosing;
        _tray.OpenRequested += OnOpenRequested;
        _tray.ExitRequested += OnExitRequested;
    }

    internal bool ResidentEnabled => _tray is not null;

    internal bool ExitRequested => _exitRequested;

    internal void RequestOpen() => _ = RequestOpenAsync(CancellationToken.None);

    internal async Task<bool> RequestOpenAsync(CancellationToken cancellationToken)
    {
        if (_disposed
            || _exitRequested
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

    internal void PrepareForSessionEnd() => BeginExit(shutdown: false);

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

    private void OnWindowClosing(object? sender, CancelEventArgs args)
    {
        if (_exitRequested || _tray is null) return;

        args.Cancel = true;
        CloseSettingsWindow();
        _window.Hide();
        Log.Main("[Startup] main window hidden; owner remains resident");
    }

    private void OnOpenRequested() => RequestOpen();

    private void OnExitRequested() => BeginExit(shutdown: true);

    private void BeginExit(bool shutdown)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            if (_window.Dispatcher.HasShutdownStarted
                || _window.Dispatcher.HasShutdownFinished)
            {
                return;
            }
            _window.Dispatcher.BeginInvoke(new Action(() => BeginExit(shutdown)));
            return;
        }
        if (_disposed || _exitRequested) return;

        _exitRequested = true;
        CloseSettingsWindow();
        StopInfrastructure();
        if (shutdown) _shutdown();
    }

    private bool OpenWindow()
    {
        if (_disposed
            || _exitRequested
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
