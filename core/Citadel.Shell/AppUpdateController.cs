using System.Windows.Threading;
using Citadel.Setting;

namespace Citadel.Shell;

/// <summary>
/// Owns the Settings updater state machine and its one-operation-at-a-time
/// worker boundary. ShellSettingHost only forwards the Setting seam to this
/// controller, matching dhepz's service/controller split.
/// </summary>
internal sealed class AppUpdateController : IDisposable
{
    private readonly IAppUpdateService _service;
    private readonly Action _requestExit;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private AppUpdateState _state;
    private bool _disposed;

    public AppUpdateController(
        IAppUpdateService service,
        Action requestExit,
        Dispatcher dispatcher)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _state = service.Snapshot;
    }

    public event Action? Changed;

    public AppUpdateState Snapshot
    {
        get
        {
            lock (_stateLock) return _state;
        }
    }

    public void Check()
    {
        lock (_stateLock)
        {
            if (_disposed || !_state.CanCheck || _state.Busy) return;
            _state = _state with
            {
                Status = "Checking for updates...",
                Progress = 0,
                CanCheck = false,
                CanInstall = false,
                Busy = true,
            };
        }

        RaiseChanged();
        _ = CompleteCheckAsync();
    }

    public void Install()
    {
        lock (_stateLock)
        {
            if (_disposed || !_state.CanInstall || _state.Busy) return;
            _state = _state with
            {
                Status = "Downloading update... 0%",
                Progress = 0,
                CanCheck = false,
                CanInstall = false,
                Busy = true,
            };
        }

        RaiseChanged();
        _ = CompleteInstallAsync();
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
    }

    private async Task CompleteCheckAsync()
    {
        try
        {
            var state = await _service
                .CheckAsync(_lifetime.Token)
                .ConfigureAwait(false);
            Publish(state);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishFailure("Update check failed", exception);
        }
    }

    private async Task CompleteInstallAsync()
    {
        try
        {
            var state = await _service
                .DownloadAsync(PublishProgress, _lifetime.Token)
                .ConfigureAwait(false);

            if (state.Progress != 100)
            {
                Publish(state);
                return;
            }

            if (!_service.TryScheduleRestart(out var error))
            {
                Publish(state with
                {
                    Status = $"Update failed: {error}",
                    CanCheck = true,
                    CanInstall = true,
                    Busy = false,
                });
                return;
            }

            OnDispatcher(() =>
            {
                if (!Apply(state)) return;
                _requestExit();
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishFailure("Update failed", exception);
        }
    }

    private void PublishProgress(int progress)
    {
        var current = Snapshot;
        Publish(current with
        {
            Status = $"Downloading update... {progress}%",
            Progress = progress,
            CanCheck = false,
            CanInstall = false,
            Busy = true,
        });
    }

    private void PublishFailure(string prefix, Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        var current = Snapshot;
        Publish(current with
        {
            Status = $"{prefix}: {message}",
            CanCheck = current.Supported,
            CanInstall = current.Available,
            Busy = false,
        });
    }

    private void Publish(AppUpdateState state) => OnDispatcher(() => Apply(state));

    private bool Apply(AppUpdateState state)
    {
        lock (_stateLock)
        {
            if (_disposed) return false;
            _state = state;
        }
        Changed?.Invoke();
        return true;
    }

    private void RaiseChanged() => OnDispatcher(() =>
    {
        lock (_stateLock)
        {
            if (_disposed) return;
        }
        Changed?.Invoke();
    });

    private void OnDispatcher(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _ = _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
