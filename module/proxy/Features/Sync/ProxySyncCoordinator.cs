using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Sync;

internal sealed record ProxySyncState(
    bool IsRunning,
    string Status,
    ProxySyncProgress? Progress = null,
    ProxySyncResult? Result = null);

internal sealed class ProxySyncCoordinator(
    ProxySyncService service,
    ProxyPoolStore poolStore,
    ProxySettingsStore settingsStore) : IDisposable
{
    private readonly ProxySyncService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ProxyPoolStore _poolStore = poolStore ?? throw new ArgumentNullException(nameof(poolStore));
    private readonly ProxySettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    private readonly object _gate = new();
    private CancellationTokenSource? _jobCancellation;
    private ProxySyncState _currentState = new(
        false,
        "Ready. Existing pool is kept until a sync commits.");
    private bool _disposed;

    public event EventHandler<ProxySyncState>? StateChanged;

    public event EventHandler? PoolCommitted;

    public bool IsRunning
    {
        get { lock (_gate) return _jobCancellation is not null; }
    }

    public ProxySyncState CurrentState
    {
        get { lock (_gate) return _currentState; }
    }

    public async Task StartAsync()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobCancellation is not null)
            {
                return;
            }
            cancellation = new CancellationTokenSource();
            _jobCancellation = cancellation;
        }

        Publish(new ProxySyncState(true, "Starting sync..."));
        ProxySyncState terminal;
        try
        {
            var banned = _poolStore.LoadBanned().Endpoints
                .Select(item => item.Canonical)
                .ToHashSet(StringComparer.Ordinal);
            var progress = new Progress<ProxySyncProgress>(value =>
                PublishProgress(cancellation, value));
            var result = await _service.RunAsync(
                _settingsStore.Load(),
                banned,
                progress,
                cancellation.Token).ConfigureAwait(false);

            if (result.Reachable.Count == 0)
            {
                terminal = new ProxySyncState(
                    false,
                    "No usable proxies; existing pool was preserved.",
                    Result: result);
            }
            else
            {
                _poolStore.Commit(result.Reachable);
                PoolCommitted?.Invoke(this, EventArgs.Empty);
                terminal = new ProxySyncState(
                    false,
                    $"Sync complete: {result.Reachable.Count} reachable from {result.Tested} checked.",
                    Result: result);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            terminal = new ProxySyncState(false, "Cancelled; existing pool was preserved.");
        }
        catch (Exception ex)
        {
            terminal = new ProxySyncState(false, "Sync failed: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_jobCancellation, cancellation))
                {
                    _jobCancellation = null;
                }
            }
            cancellation.Dispose();
        }

        // Publish an enabled Start button only after the coordinator has
        // released the old job slot. Otherwise an immediate Start after Stop
        // can be accepted by the UI but ignored by the still-owned slot.
        Publish(terminal);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _jobCancellation?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _jobCancellation?.Cancel();
        }
    }

    private void Publish(ProxySyncState state)
    {
        EventHandler<ProxySyncState>? handlers;
        lock (_gate)
        {
            _currentState = state;
            handlers = StateChanged;
        }
        handlers?.Invoke(this, state);
    }

    private void PublishProgress(
        CancellationTokenSource owner,
        ProxySyncProgress progress)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_jobCancellation, owner)) return;
        }
        Publish(new ProxySyncState(true, ProgressText(progress), progress));
    }

    private static string ProgressText(ProxySyncProgress progress) =>
        progress.Phase == "Fetching sources"
            ? $"Fetching source {progress.SourcesCompleted}/{progress.SourcesTotal} · {progress.Candidates} unique"
            : $"Checking {progress.Tested}/{progress.Candidates} · {progress.Reachable} reachable · {progress.Failed} failed";
}
