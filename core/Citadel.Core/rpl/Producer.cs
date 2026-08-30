namespace Citadel.Core.Rpl;

/// <summary>
/// Thin cold port of rpl::producer: a generator that runs fresh per
/// subscription. Cold matters — a port built on a hot event silently
/// diverges on every re-subscription.
/// </summary>
public sealed class Producer<T>
{
    private readonly Func<Action<T>, Lifetime> _generator;

    public Producer(Func<Action<T>, Lifetime> generator) => _generator = generator;

    /// <summary>Cold subscribe: runs the generator, returns the unwire lifetime.</summary>
    public Lifetime Start(Action<T> next) => _generator(next);

    /// <summary>Cold subscribe owned by <paramref name="into"/>.</summary>
    public void Subscribe(Action<T> next, Lifetime into)
    {
        into.Add(Start(next).Destroy);
    }

    /// <summary>One value, then silence.</summary>
    public static Producer<T> Single(T value) =>
        new(next =>
        {
            next(value);
            return new Lifetime();
        });

    /// <summary>
    /// Wrap an EventStream so it can feed the operator chain. The
    /// *subscription* is fresh per Start, but the stream stays hot — a
    /// subscriber sees values from its own subscription onward, with no
    /// replay of what it missed. Anything needing replay should use
    /// Variable, whose two-arg Subscribe fires with the current value first.
    /// </summary>
    public static Producer<T> FromStream(EventStream<T> stream) =>
        new(stream.Subscribe);
}
