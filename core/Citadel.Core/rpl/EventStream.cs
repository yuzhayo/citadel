namespace Citadel.Core.Rpl;

/// <summary>
/// Port of rpl::event_stream — a multi-subscriber broadcast point. Fire
/// is synchronous on the calling thread; fire on main (or marshal via
/// Core.Crl) when subscribers touch WPF objects.
///
/// Two deliberate divergences from upstream are retained:
/// the subscriber snapshot is taken under a lock (upstream is documented
/// not thread-safe), and each subscriber is invoked inside its own
/// try/catch — upstream has none, and a throw there permanently corrupts
/// the stream's bookkeeping.
/// </summary>
public sealed class EventStream<T>
{
    private readonly object _gate = new();
    private List<(int Id, Action<T> Next)>? _subs;
    private int _nextId;

    /// <summary>Subscribe; returns a lifetime whose death unwires you.</summary>
    public Lifetime Subscribe(Action<T> next)
    {
        lock (_gate)
        {
            var id = ++_nextId;
            (_subs ??= new()).Add((id, next));
            var lt = new Lifetime();
            lt.Add(() =>
            {
                lock (_gate)
                {
                    _subs?.RemoveAll(s => s.Id == id);
                }
            });
            return lt;
        }
    }

    /// <summary>Subscribe with the cleanup absorbed by an owner lifetime.</summary>
    public void Subscribe(Action<T> next, Lifetime into)
    {
        into.Add(Subscribe(next).Destroy);
    }

    public void Fire(T value)
    {
        Action<T>[] snapshot;
        lock (_gate)
        {
            if (_subs is null || _subs.Count == 0) return;
            snapshot = new Action<T>[_subs.Count];
            for (var i = 0; i < _subs.Count; i++) snapshot[i] = _subs[i].Next;
        }
        foreach (var next in snapshot)
        {
            try
            {
                next(value);
            }
            catch (Exception ex)
            {
                Log.Main($"[Rpl] subscriber threw, delivery continues: {ex.Message}");
            }
        }
    }
}
