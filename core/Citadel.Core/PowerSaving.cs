namespace Citadel.Core.Crl;

/// <summary>
/// Snap, not freeze: disabling animations jumps them to their final
/// value (upstream anim::Disabled semantics); there is no frozen state
/// to resume from. Lives in Citadel.Core.Crl — not its own namespace —
/// because that is where compatible citizens look for it.
///
/// Polarity is v0's and is pinned by tests: Enabled == true means
/// saving is ON — stop timers, skip ticks. Gateway does
/// `if (PowerSaving.Enabled) timer.Stop(); else timer.Start();`.
/// </summary>
public static class PowerSaving
{
    [Flags]
    public enum Flags
    {
        None = 0,
        Animations = 1,
        All = ~0,
    }

    private static readonly object Gate = new();
    private static Flags _current = Flags.None;

    /// <summary>Fires whenever the effective flags change. A real event: v0 does += and -=.</summary>
    public static event Action? Changed;

    public static Flags Current
    {
        get { lock (Gate) return _current; }
    }

    public static bool On(Flags flag) => (Current & flag) != 0;

    /// <summary>Diffs, then fires — only on real change.</summary>
    public static void Set(Flags flags)
    {
        bool changed;
        lock (Gate)
        {
            changed = _current != flags;
            _current = flags;
        }
        if (changed) Changed?.Invoke();
    }

    // --- v0 compatibility surface, unchanged semantics ---

    /// <summary>True while saving is ON (v0 polarity).</summary>
    public static bool Enabled => On(Flags.Animations);

    /// <summary>
    /// The v0 writer controls animation work. It maps to Animations rather
    /// than All so adding a future flag cannot silently broaden this legacy
    /// API's meaning.
    /// </summary>
    public static void Set(bool enabled) => Set(enabled ? Flags.Animations : Flags.None);

    /// <summary>Test hook: back to a clean slate.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _current = Flags.None;
            Changed = null;
        }
    }
}
