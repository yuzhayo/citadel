using System.Collections.Concurrent;
using Citadel.Core.Rpl;

namespace Citadel.Core.Crl;

/// <summary>
/// Port of lib_crl's main-queue discipline. Ownership is inverted from
/// the obvious shape: the queue does not own a dispatcher — the app
/// injects the wake delegate (e.g. Dispatcher.BeginInvoke), which is
/// what keeps Citadel.Core free of WPF types.
///
/// Semantics that must not drift (wiki/Crl.md):
///  - never inline: a post from the main thread still defers to the next drain;
///  - wake coalescing: one gate flag, one wake per non-empty batch;
///  - generation-batched drain: posts enqueued during a drain run in the
///    next one, never recursively;
///  - FIFO ordering is a guarantee.
/// </summary>
public sealed class MainQueue
{
    private readonly Action<Action> _wake;
    private readonly Func<bool>? _isMain;
    private readonly ConcurrentQueue<Action> _pending = new();
    private int _wakeScheduled;

    /// <param name="wake">Injected processor: schedules a drain on the real main loop.</param>
    /// <param name="isMain">Optional main-thread probe; defaults to true.</param>
    public MainQueue(Action<Action> wake, Func<bool>? isMain = null)
    {
        _wake = wake ?? throw new ArgumentNullException(nameof(wake));
        _isMain = isMain;
    }

    public bool IsMain => _isMain?.Invoke() ?? true;

    /// <summary>Unguarded post — the exception, not the default.</summary>
    public void Post(Action action)
    {
        _pending.Enqueue(action ?? throw new ArgumentNullException(nameof(action)));
        Wake();
    }

    /// <summary>
    /// Guarded post — the default idiom. The guard is checked at
    /// execution time: if the lifetime is dead when the queue reaches
    /// the action, it is dropped. Stale background results never touch
    /// dead views.
    /// </summary>
    public void Post(Lifetime lifetime, Action action)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(action);
        _pending.Enqueue(() =>
        {
            if (lifetime.Alive) action();
        });
        Wake();
    }

    private void Wake()
    {
        if (Interlocked.CompareExchange(ref _wakeScheduled, 1, 0) != 0) return;
        try
        {
            _wake(Drain);
        }
        catch (Exception ex)
        {
            // The injected wake is Dispatcher.BeginInvoke in production, which
            // throws once the dispatcher starts shutting down. Leaving the flag
            // set would wedge the queue permanently and silently: every later
            // Post would coalesce into a wake that is never coming.
            Interlocked.Exchange(ref _wakeScheduled, 0);
            Log.Main($"[Crl] wake failed, next post will retry: {ex.Message}");
        }
    }

    /// <summary>
    /// Invoked by the injected processor on the main thread. Flushes one
    /// generation: items enqueued while this batch runs stay queued and
    /// get a fresh wake afterwards — breadth-first by generation, never
    /// depth-first recursion.
    /// </summary>
    public void Drain()
    {
        var batch = new List<Action>();
        while (_pending.TryDequeue(out var item)) batch.Add(item);

        foreach (var item in batch)
        {
            try
            {
                item();
            }
            catch (Exception ex)
            {
                Log.Main($"[Crl] posted action threw: {ex}");
            }
        }

        Interlocked.Exchange(ref _wakeScheduled, 0);
        if (!_pending.IsEmpty) Wake();
    }
}
