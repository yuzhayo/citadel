namespace Citadel.Core.Crl;

/// <summary>
/// Citadel original, not a port: upstream's deadlock detector is opt-in,
/// pings every 60 s, and crashes instead of logging. This one pings every
/// second and traces stalls over 100 ms to Log.
///
/// What is measured is ping→pong latency — how long the main thread took
/// to answer — not "time since the last pong". The difference matters: the
/// latter is naturally about one interval on a perfectly healthy thread, so
/// at the production settings (1000 ms interval, 100 ms threshold) it
/// reports a stall every single second. Latency is ~0 when healthy.
///
/// A full wedge is still caught, because an unanswered ping is itself the
/// signal: the timer side notices the outstanding ping and reports its age.
/// Pings never stack — one is outstanding at a time.
/// </summary>
public sealed class Watchdog : IDisposable
{
    private readonly MainQueue _main;
    private readonly int _stallMs;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private Timer? _timer;
    private long _pingSentTs;
    private int _awaitingPong;

    public Watchdog(MainQueue main, int stallMs = 100)
        : this(main, stallMs, TimeProvider.System)
    {
    }

    internal Watchdog(MainQueue main, int stallMs, TimeProvider timeProvider)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _stallMs = stallMs;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Start(int intervalMs = 1000)
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => Poll(), null, intervalMs, intervalMs);
        }
    }

    /// <summary>
    /// Performs one timer poll. Kept as an internal seam so latency behavior
    /// can be tested against a controlled clock instead of depending on the
    /// host scheduler's timing precision.
    /// </summary>
    internal void Poll()
    {
        // Previous ping still unanswered: the main thread is silent. Report
        // how long it has been silent and do not queue another ping behind it.
        if (Interlocked.CompareExchange(ref _awaitingPong, 1, 0) == 1)
        {
            var silentMs = _timeProvider
                .GetElapsedTime(Interlocked.Read(ref _pingSentTs))
                .TotalMilliseconds;
            if (silentMs > _stallMs)
            {
                Log.Main($"[Crl] main-thread stall: {silentMs:F0} ms, ping unanswered");
            }
            return;
        }

        var sent = _timeProvider.GetTimestamp();
        Interlocked.Exchange(ref _pingSentTs, sent);

        _main.Post(() =>
        {
            var lagMs = _timeProvider.GetElapsedTime(sent).TotalMilliseconds;
            Interlocked.Exchange(ref _awaitingPong, 0);
            if (lagMs > _stallMs)
            {
                Log.Main($"[Crl] main-thread stall: {lagMs:F0} ms");
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
