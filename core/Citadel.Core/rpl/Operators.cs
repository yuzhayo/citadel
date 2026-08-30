using Citadel.Core.Crl;

namespace Citadel.Core.Rpl;

public static class Operators
{
    /// <summary>
    /// Suppress consecutive duplicates. Real upstream operator
    /// (distinct_until_changed.h). This is the only place de-duplication
    /// happens — Variable.Value fires on every set, by design, so
    /// suppression stays the caller's explicit choice.
    /// </summary>
    public static Producer<T> DistinctUntilChanged<T>(this Producer<T> source) =>
        new(next =>
        {
            var hasLast = false;
            var last = default(T)!;
            return source.Start(value =>
            {
                if (hasLast && EqualityComparer<T>.Default.Equals(last, value)) return;
                hasLast = true;
                last = value;
                next(value);
            });
        });

    /// <summary>
    /// Citadel invention — no rpl precedent, nothing in scope streams
    /// yet, so it ships unexercised until a real push source exists.
    /// Delivers on the main queue, latest-wins: N values arriving between
    /// two drains collapse into one delivery of the newest.
    /// </summary>
    public static Producer<T> BatchOnMain<T>(this Producer<T> source, MainQueue main) =>
        new(next =>
        {
            var lifetime = new Lifetime();
            var state = new BatchState<T>(main, next, lifetime);
            lifetime.Add(source.Start(state.OnNext));
            return lifetime;
        });

    private sealed class BatchState<T>(MainQueue main, Action<T> next, Lifetime lifetime)
    {
        private readonly object _gate = new();
        private T _pending = default!;
        private bool _hasPending;
        private bool _scheduled;

        public void OnNext(T value)
        {
            lock (_gate)
            {
                _pending = value;
                _hasPending = true;
                if (_scheduled) return;
                _scheduled = true;
            }
            main.Post(lifetime, Deliver);
        }

        private void Deliver()
        {
            T value;
            lock (_gate)
            {
                if (!_hasPending)
                {
                    _scheduled = false;
                    return;
                }
                value = _pending;
                _pending = default!;
                _hasPending = false;
                _scheduled = false;
            }
            next(value);
        }
    }
}
