namespace Module.Camoprof.Network;

internal sealed class NetworkMonitor : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecoveryConfirmDelay = TimeSpan.FromMilliseconds(300);

    private readonly NetworkProbe _probe;
    private readonly Queue<NetworkSample> _samples = new();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private Task? _loop;
    private NetworkSnapshot _current = NetworkSnapshot.Initial;
    private int _disposed;

    public NetworkMonitor(NetworkProbe? probe = null)
        => _probe = probe ?? new NetworkProbe();

    public event EventHandler<NetworkSnapshot>? SnapshotChanged;

    public NetworkSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_sync)
        {
            _loop ??= RunAsync(_lifetime.Token);
        }
    }

    public async Task<NetworkSnapshot> RefreshForProviderCheckAsync(
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var first = await SampleOnceAsync(linked.Token).ConfigureAwait(false);
        if (first.State != NetworkState.Recovering)
        {
            return first;
        }

        await Task.Delay(RecoveryConfirmDelay, linked.Token).ConfigureAwait(false);
        return await SampleOnceAsync(linked.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _probe.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(SampleInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal CamoProf lifetime end.
        }
    }

    private async Task<NetworkSnapshot> SampleOnceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sample = await _probe.SampleAsync(cancellationToken).ConfigureAwait(false);
            NetworkSnapshot snapshot;
            lock (_sync)
            {
                _samples.Enqueue(sample);
                while (_samples.Count > 4)
                {
                    _samples.Dequeue();
                }

                snapshot = NetworkPolicy.Classify(_samples.ToArray());
                _current = snapshot;
            }

            SnapshotChanged?.Invoke(this, snapshot);
            return snapshot;
        }
        finally
        {
            _sampleGate.Release();
        }
    }
}
