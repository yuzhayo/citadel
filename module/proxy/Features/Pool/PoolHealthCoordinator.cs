using System.Diagnostics;
using CitadelBridge;
using Module.Proxy.Features.Sync;
using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Pool;

internal sealed record PoolHealthProgress(bool IsRunning, int Checked, int Total, int Healthy, int Failed, string Status);

/// <summary>Rechecks the already committed pool. It never fetches sources or republishes proxy.txt.</summary>
internal sealed class PoolHealthCoordinator(
    ProxyPoolStore store,
    ProxySettingsStore settingsStore,
    IProxyReachabilityProbe probe) : IDisposable
{
    private readonly ProxyPoolStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ProxySettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    private readonly IProxyReachabilityProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public event EventHandler<PoolHealthProgress>? StateChanged;
    public event EventHandler? HealthSaved;

    public bool IsRunning { get { lock (_gate) return _cancellation is not null; } }

    public async Task RefreshAsync()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellation is not null) return;
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
        }

        var endpoints = _store.LoadActive().Endpoints;
        if (endpoints.Length == 0)
        {
            Complete(cancellation, new PoolHealthProgress(false, 0, 0, 0, 0, "No committed proxies to check."));
            return;
        }

        Publish(new PoolHealthProgress(true, 0, endpoints.Length, 0, 0, "Checking committed proxy health..."));
        try
        {
            var settings = _settingsStore.Load();
            var records = new ProxyHealthRecord[endpoints.Length];
            var gate = new object();
            var next = -1;
            var checkedCount = 0;
            var healthy = 0;
            var failed = 0;
            var workerCount = Math.Min(settings.ParallelTcpChecks, endpoints.Length);
            var workers = Enumerable.Range(0, workerCount).Select(async _ =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= endpoints.Length) return;
                    var endpoint = endpoints[index];
                    var result = await _probe.ProbeAsync(
                        endpoint,
                        TimeSpan.FromSeconds(settings.ProxyValidationTimeoutSeconds),
                        cancellation.Token).ConfigureAwait(false);
                    records[index] = result.ToHealthRecord(endpoint, DateTimeOffset.UtcNow);
                    int checkedNow;
                    int healthyNow;
                    int failedNow;
                    lock (gate)
                    {
                        checkedNow = ++checkedCount;
                        if (result.IsHealthy) healthyNow = ++healthy;
                        else healthyNow = healthy;
                        failedNow = result.IsHealthy ? failed : ++failed;
                    }
                    Publish(new PoolHealthProgress(true, checkedNow, endpoints.Length, healthyNow, failedNow,
                        $"Checking {checkedNow}/{endpoints.Length} · {healthyNow} healthy · {failedNow} failed"));
                }
            }).ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            _store.SaveHealth(records, endpoints);
            HealthSaved?.Invoke(this, EventArgs.Empty);
            Complete(cancellation, new PoolHealthProgress(false, checkedCount, endpoints.Length, healthy, failed,
                $"Health refresh complete: {healthy} healthy · {failed} failed."));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Complete(cancellation, new PoolHealthProgress(false, 0, endpoints.Length, 0, 0, "Health refresh cancelled; previous diagnostics were kept."));
        }
        catch (Exception ex)
        {
            Complete(cancellation, new PoolHealthProgress(false, 0, endpoints.Length, 0, 0, "Health refresh failed: " + ex.Message));
        }
    }

    public void Stop()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation?.Cancel();
        }
    }

    private void Publish(PoolHealthProgress state) => StateChanged?.Invoke(this, state);

    private void Complete(CancellationTokenSource owner, PoolHealthProgress state)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cancellation, owner)) _cancellation = null;
        }
        owner.Dispose();
        Publish(state);
    }
}
