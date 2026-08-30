namespace Citadel.Core.Rpl;

/// <summary>
/// Port of rpl::variable with the three members upstream actually has:
/// Current (synchronous getter), Value (settable, fires on change), and
/// Changes (change stream with no initial value).
///
/// Compatibility-critical for existing citizens: Value must stay
/// a settable T — rpl names its producer value(), and porting that naming
/// would turn Value into a stream and break _heartbeat.Value = "...".
///
/// Not thread-safe by design (rpl is single-threaded; cross-thread moves
/// are the main queue's job). Marshal with MainQueue before setting from
/// a background thread.
/// </summary>
public sealed class Variable<T>
{
    private T _value;

    public Variable(T initial) => _value = initial;

    /// <summary>Change stream, no initial value.</summary>
    public EventStream<T> Changes { get; } = new();

    /// <summary>Synchronous getter.</summary>
    public T Current => _value;

    /// <summary>
    /// Settable value; fires Changes on the calling thread on every set.
    ///
    /// Fires unconditionally, matching v0 (Core.Rpl/Variable.cs:15-23).
    /// It deliberately does NOT suppress equal values: for a reference type
    /// whose contents were mutated in place, the reference compares equal, so
    /// de-duplicating here would silently swallow a real change and leave the
    /// UI stale. Callers who want suppression opt into
    /// <see cref="Operators.DistinctUntilChanged"/>, which is why that
    /// operator ships.
    /// </summary>
    public T Value
    {
        get => _value;
        set
        {
            _value = value;
            Changes.Fire(value);
        }
    }

    /// <summary>
    /// v0-compatible replay-1 subscription: fires now with the current
    /// value, then on every change, owned by <paramref name="into"/>.
    /// </summary>
    public void Subscribe(Action<T> next, Lifetime into)
    {
        next(_value);
        Changes.Subscribe(next, into);
    }
}
