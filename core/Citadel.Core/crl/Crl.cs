using Citadel.Core.Rpl;

namespace Citadel.Core.Crl;

/// <summary>
/// Compatibility surface for existing citizens that compile against this
/// namespace. Forwards to the injected
/// MainQueue instance; the deliberate change is that Post never runs
/// inline, and Initialize takes the WPF-free MainQueue instead of a
/// Dispatcher, since Citadel.Core must not reference WPF.
/// </summary>
public static class Crl
{
    private static MainQueue? _main;
    private static Watchdog? _watchdog;

    /// <summary>Call once at startup, on the main thread.</summary>
    public static void Initialize(MainQueue main) =>
        _main = main ?? throw new ArgumentNullException(nameof(main));

    /// <summary>True when the calling thread IS the main thread.</summary>
    public static bool IsMain => _main?.IsMain ?? true;

    /// <summary>
    /// Blocking marshal: runs now on main, otherwise posts and waits.
    /// Edge-case API only — the everyday path is Post.
    /// </summary>
    public static void Run(Action action)
    {
        var m = _main;
        if (m is null || m.IsMain)
        {
            action();
            return;
        }
        using var done = new SemaphoreSlim(0, 1);
        m.Post(() =>
        {
            try { action(); }
            finally { done.Release(); }
        });

        // Behaviour is unchanged — this still waits as long as it takes. The
        // point of the first bounded wait is that a wedged main thread stops
        // being an invisible hang: without it, a caller blocks here forever
        // with nothing in the log to say where or why.
        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Main("[Crl] Run has waited 5s for the main thread; still waiting.");
            done.Wait();
        }
    }

    /// <summary>Fire-and-forget marshal. No-op before Initialize (crl semantics).</summary>
    public static void Post(Action action)
    {
        if (_main is null)
        {
            // Silence here would be undebuggable: an App that wires Initialize
            // late loses every posted action with no trace. The required ordering
            // (Log.Start before Crl.Initialize) only helps if the drop is recorded.
            Log.Main("[Crl] Post before Initialize — action dropped.");
            return;
        }
        _main.Post(action);
    }

    /// <summary>Guarded fire-and-forget marshal.</summary>
    public static void Post(Lifetime lifetime, Action action)
    {
        if (_main is null)
        {
            Log.Main("[Crl] guarded Post before Initialize — action dropped.");
            return;
        }
        _main.Post(lifetime, action);
    }

    /// <summary>1 s ping, stall traces over stallMs to Log.</summary>
    public static void StartWatchdog(int intervalMs = 1000, int stallMs = 100)
    {
        var m = _main ?? throw new InvalidOperationException("Call Crl.Initialize first.");
        _watchdog ??= new Watchdog(m, stallMs);
        _watchdog.Start(intervalMs);
    }

    /// <summary>Test hook: back to the uninitialized state.</summary>
    internal static void ResetForTests()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        _main = null;
    }
}
