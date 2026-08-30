using Citadel.Core;
using Citadel.Core.Crl;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Citadel.Shell;

/// <summary>
/// Why a registration was refused. Settings lists these, so the reason has to
/// survive as data rather than only as a log line.
/// </summary>
public enum RegistrationRefusal { DuplicateRoute, ReservedRoute, ViewFailed }

/// <summary>One refused registration, kept for Settings and for the log.</summary>
public sealed record RegistrationFailure(
    string Route,
    RegistrationRefusal Reason,
    string Message);

/// <summary>
/// The registry core exposes and waits on. It has no idea screens come from
/// disk: no folder path, no manifest, no load context. A fake descriptor can
/// exercise the registry without filesystem discovery.
///
/// Two things the gate does because callers cannot be trusted to:
///
/// - Every mutation goes through MainQueue.Post first. The searcher's watcher
///   raises on a threadpool thread, so an unmarshalled Register would mutate
///   WPF state off-thread. MainQueue.Post never runs inline, so the registry is
///   unchanged when Register returns even on the main thread — that is the
///   guarded-post discipline doing its job, not a bug to work around.
/// - Reserved routes never pass. A module claiming `settings` would collide
///   with nothing and silently shadow navigation.
///
/// Order is Order, then Title, then Route. v0 sorted on Order then Title
/// (ModuleLoader.cs:58-61), which leaves two entries sharing both free to swap
/// places between runs.
/// </summary>
public sealed class ModuleGate : IModuleGate
{
    /// <summary>
    /// Routes core owns; a screen may never claim one.
    /// `settings/gallery` is also a built-in view — a citizen claiming it would
    /// collide with nothing and silently shadow navigation.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedRoutes =
    [
        "settings",
        "settings/appearance",
        "settings/layout",
        "settings/gallery",
    ];

    private readonly MainQueue _main;
    private readonly Lifetime _lifetime;
    private readonly List<ModuleDescriptor> _registered = [];
    private readonly List<RegistrationFailure> _failures = [];

    public ModuleGate(MainQueue main, Lifetime lifetime)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    /// <summary>Fires on the main thread after the registry changed.</summary>
    public event Action? RegistryChanged;

    /// <summary>Fires on the main thread after a registration was refused.</summary>
    public event Action<RegistrationFailure>? RegistrationRefused;

    /// <summary>Deterministic snapshot. Settings and the sidebar read this.</summary>
    public IReadOnlyList<ModuleDescriptor> Snapshot() => [.. _registered];

    /// <summary>Every refusal so far, for Settings' failure list.</summary>
    public IReadOnlyList<RegistrationFailure> Failures() => [.. _failures];

    public void Register(ModuleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _main.Post(_lifetime, () => RegisterOnMain(descriptor));
    }

    public void Unregister(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _main.Post(_lifetime, () => UnregisterOnMain(route));
    }

    /// <summary>
    /// Drops a citizen whose view could not be created, so a screen that cannot
    /// render stops being offered. Already on main: the Router calls it from
    /// inside a navigation.
    /// </summary>
    internal void RejectForFailedView(string route, string message)
    {
        var failure = new RegistrationFailure(route, RegistrationRefusal.ViewFailed, message);
        _failures.Add(failure);
        Log.Modules($"[Gate] '{route}' failed to create a view: {message}");
        var removed = _registered.RemoveAll(d => Same(d.Route, route)) > 0;
        RegistrationRefused?.Invoke(failure);
        if (removed) RegistryChanged?.Invoke();
    }

    private void RegisterOnMain(ModuleDescriptor descriptor)
    {
        if (ReservedRoutes.Any(reserved => Same(reserved, descriptor.Route)))
        {
            Refuse(descriptor.Route, RegistrationRefusal.ReservedRoute,
                $"'{descriptor.Route}' is a reserved core route");
            return;
        }
        if (_registered.Any(existing => Same(existing.Route, descriptor.Route)))
        {
            Refuse(descriptor.Route, RegistrationRefusal.DuplicateRoute,
                $"'{descriptor.Route}' is already registered");
            return;
        }

        _registered.Add(descriptor);
        _registered.Sort(Compare);
        Log.Modules($"[Gate] registered '{descriptor.Route}'");
        RegistryChanged?.Invoke();
    }

    private void UnregisterOnMain(string route)
    {
        if (_registered.RemoveAll(d => Same(d.Route, route)) == 0)
        {
            // Not a failure: the searcher may see a folder vanish that never
            // registered, and a refused duplicate is deleted later like any
            // other. Recorded so it is not invisible either.
            Log.Modules($"[Gate] unregister '{route}' ignored; not registered");
            return;
        }

        Log.Modules($"[Gate] unregistered '{route}'");
        RegistryChanged?.Invoke();
    }

    private void Refuse(string route, RegistrationRefusal reason, string message)
    {
        var failure = new RegistrationFailure(route, reason, message);
        _failures.Add(failure);
        Log.Modules($"[Gate] rejected: {message}");
        RegistrationRefused?.Invoke(failure);
    }

    private static int Compare(ModuleDescriptor left, ModuleDescriptor right)
    {
        var byOrder = left.Order.CompareTo(right.Order);
        if (byOrder != 0) return byOrder;
        var byTitle = string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
        return byTitle != 0
            ? byTitle
            : string.Compare(left.Route, right.Route, StringComparison.Ordinal);
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}
