namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>Admission and settled execution handles; durable state stays in Queue.</summary>
internal sealed class QueueScheduler(
    object gate, Func<IReadOnlyList<DownloadJobRecord>> snapshot,
    Func<DownloadJobRecord, CancellationToken, Task> execute)
{
    internal const int AutomaticLimit = 4;
    private readonly Dictionary<string, Execution> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _manual = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public void Start()
    {
        lock (gate) _loop ??= Task.Run(LoopAsync);
        Wake();
    }

    public bool IsActive(string id) { lock (gate) return _active.ContainsKey(id); }
    public void MarkManual(string id) { lock (gate) if (!_active.ContainsKey(id)) _manual.Add(id); }
    public void Wake() { if (_wake.CurrentCount == 0) { try { _wake.Release(); } catch (SemaphoreFullException) { } } }

    public Task Cancel(IEnumerable<string> ids)
    {
        lock (gate)
        {
            var settling = new List<Task>();
            foreach (var id in ids)
            {
                _manual.Remove(id);
                if (!_active.TryGetValue(id, out var active)) continue;
                active.Cancel.Cancel();
                settling.Add(active.Settled.Task);
            }
            Wake();
            return Task.WhenAll(settling);
        }
    }

    public async Task ShutdownAsync()
    {
        _lifetime.Cancel();
        Task settled;
        lock (gate) settled = Cancel(_active.Keys.ToArray());
        await settled.ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                lock (gate)
                {
                    var automatic = _active.Values.Count(item => !item.Manual);
                    foreach (var job in snapshot().Where(item => item.State == DownloadJobState.Queued))
                    {
                        if (_active.ContainsKey(job.JobId)) continue;
                        var manual = _manual.Contains(job.JobId);
                        if (!manual && automatic >= AutomaticLimit) continue;
                        if (_active.Values.Any(item => string.Equals(item.Target, job.Target.FilePath,
                                StringComparison.OrdinalIgnoreCase))) continue;
                        var entry = new Execution(manual, job.Target.FilePath,
                            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                        _active.Add(job.JobId, entry);
                        _manual.Remove(job.JobId);
                        if (!manual) automatic++;
                        _ = Task.Run(() => ExecuteAsync(job, entry));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ExecuteAsync(DownloadJobRecord job, Execution entry)
    {
        try { await execute(job, entry.Cancel.Token).ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                _active.Remove(job.JobId);
                entry.Cancel.Dispose();
                entry.Settled.TrySetResult();
            }
            Wake();
        }
    }

    private sealed record Execution(bool Manual, string Target, CancellationTokenSource Cancel)
    {
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
