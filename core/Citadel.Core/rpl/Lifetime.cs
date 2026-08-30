namespace Citadel.Core.Rpl;

/// <summary>
/// Port of rpl::lifetime: a bag of destroy-callbacks executed LIFO when
/// the lifetime dies. Every subscription in this app must be owned by a
/// Lifetime — leaks become structurally impossible instead of merely
/// discouraged.
///
/// Adding to an already-dead lifetime executes the callback immediately.
/// This matches rpl's consumer protocol (consumer.h:101-109 destroys an
/// added lifetime once terminated), and it is always the right call for
/// the common case "stop this timer / detach this handler".
///
/// Thread-safe. EventStream hands out Lifetimes and locks its own
/// subscriber list, so the lifetime it returns has to be equally safe or
/// the guarantee is only half there: Fire on a background thread racing
/// Destroy on main would corrupt the callback list.
/// </summary>
public sealed class Lifetime
{
    private readonly object _gate = new();
    private List<Action>? _callbacks;
    private volatile bool _alive = true;

    public bool Alive => _alive;

    /// <summary>Register a cleanup. Runs now if already dead.</summary>
    public void Add(Action destroy)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        lock (_gate)
        {
            if (_alive)
            {
                (_callbacks ??= new()).Add(destroy);
                return;
            }
        }
        // Outside the lock: a cleanup may take locks of its own.
        destroy();
    }

    /// <summary>
    /// Make this lifetime own another's death. The child stays alive and
    /// keeps accepting registrations; it is destroyed when we are.
    ///
    /// Deliberately NOT v0's shape. v0 drained the child's callback list
    /// and set Alive=false, which had two different wrong outcomes
    /// depending on a count: a child with callbacks would run any *later*
    /// registration immediately, while the resource was still live; a
    /// child with none stayed Alive with nothing absorbed, so its later
    /// registrations were orphaned and never ran at all. Owning
    /// child.Destroy has neither hole and is less code.
    /// </summary>
    public void Add(Lifetime other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, this)) return;
        Add(other.Destroy);
    }

    public static Lifetime operator +(Lifetime lt, Action destroy)
    {
        lt.Add(destroy);
        return lt;
    }

    /// <summary>Run all callbacks newest-first and die. Idempotent.</summary>
    public void Destroy()
    {
        List<Action>? callbacks;
        lock (_gate)
        {
            if (!_alive) return;
            _alive = false;
            callbacks = _callbacks;
            _callbacks = null;
        }
        if (callbacks is null) return;

        for (var i = callbacks.Count - 1; i >= 0; i--)
        {
            try
            {
                callbacks[i]();
            }
            catch (Exception ex)
            {
                // Same reasoning as EventStream's per-subscriber catch: one
                // bad cleanup must not strand every cleanup behind it.
                Log.Main($"[Rpl] lifetime cleanup threw, unwind continues: {ex.Message}");
            }
        }
    }
}
