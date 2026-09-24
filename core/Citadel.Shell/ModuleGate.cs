using Citadel.Core;
using Citadel.Core.Crl;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Citadel.Shell;

/// <summary>
/// Why a registration was refused. Settings lists these, so the reason has to
/// survive as data rather than only as a log line.
/// </summary>
public enum RegistrationRefusal
{
    DuplicateRoute,
    ReservedRoute,
    ViewFailed,
    ResidentStartFailed,
}

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
/// Things the gate does because callers cannot be trusted to:
///
/// - Every mutation goes through MainQueue.Post first. The searcher's watcher
///   raises on a threadpool thread, so an unmarshalled Register would mutate
///   WPF state off-thread. MainQueue.Post never runs inline, so the registry is
///   unchanged when Register returns even on the main thread — that is the
///   guarded-post discipline doing its job, not a bug to work around.
/// - Reserved routes never pass. A module claiming `settings` would collide
///   with nothing and silently shadow navigation.
/// - Actual Unregister (Gate 6) is staged: when a release handler is bound,
///   Unregister marks the route Unregistering, blocks Start, awaits the
///   handler's safe Stop, and only then removes the descriptor. Failure keeps
///   the route as RemovalPending for Retry removal. Option A: rediscovery while
///   unregistering holds one pending replacement that registers atomically
///   after release succeeds.
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
        "settings/sidebar-groups",
    ];

    private readonly MainQueue _main;
    private readonly Lifetime _lifetime;
    private readonly List<ModuleDescriptor> _registered = [];
    private readonly List<RegistrationFailure> _failures = [];
    private readonly Dictionary<string, UnregisterWork> _unregisterWork = new(StringComparer.Ordinal);
    private Func<string, Task<RuntimeReleaseResult>>? _releaseHandler;

    public ModuleGate(MainQueue main, Lifetime lifetime)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    /// <summary>Fires on the main thread after the registry changed.</summary>
    public event Action? RegistryChanged;

    /// <summary>Fires on the main thread after a registration was refused.</summary>
    public event Action<RegistrationFailure>? RegistrationRefused;

    /// <summary>
    /// Fires on the main thread when a route enters or leaves RemovalPending
    /// (and when staged release completes). Hosts refresh Retry removal chrome.
    /// </summary>
    public event Action<string>? RemovalStateChanged;

    /// <summary>Deterministic snapshot. Settings and the sidebar read this.</summary>
    public IReadOnlyList<ModuleDescriptor> Snapshot() => [.. _registered];

    /// <summary>Every refusal so far, for Settings' failure list.</summary>
    public IReadOnlyList<RegistrationFailure> Failures() => [.. _failures];

    /// <summary>
    /// Shell composition only: wire coordinator.ReleaseRouteAsync before the
    /// Searcher watcher starts. Without a handler, Unregister keeps the legacy
    /// immediate-remove path (no release, no RemovalPending).
    /// </summary>
    internal void BindRuntimeReleaseHandler(Func<string, Task<RuntimeReleaseResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _releaseHandler = handler;
    }

    /// <summary>True while staged release is in flight or RemovalPending.</summary>
    internal bool IsStartBlocked(string route) =>
        !string.IsNullOrEmpty(route) && _unregisterWork.ContainsKey(route);

    /// <summary>Any route still in staged release (for tests / drain loops).</summary>
    internal bool HasPendingReleaseWork
    {
        get
        {
            foreach (var work in _unregisterWork.Values)
            {
                if (work.ReleaseRunning) return true;
            }
            return false;
        }
    }

    internal bool IsRemovalPending(string route) =>
        _unregisterWork.TryGetValue(route, out var work)
        && work.Phase == UnregisterPhase.RemovalPending;

    internal string? RemovalError(string route) =>
        _unregisterWork.TryGetValue(route, out var work) ? work.Error : null;

    /// <summary>
    /// Retry a failed safe release. Uses the in-memory descriptor; does not
    /// Start and does not need the source folder to exist.
    /// </summary>
    internal void RetryRemoval(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _main.Post(_lifetime, () =>
        {
            if (!_unregisterWork.TryGetValue(route, out var work)) return;
            if (work.Phase != UnregisterPhase.RemovalPending || work.ReleaseRunning) return;
            StartReleaseOnMain(route, work);
        });
    }

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
            // Option A: rediscovery while the old descriptor is still registered
            // (Unregistering / RemovalPending). Hold one pending replacement so
            // it registers atomically after release — no duplicate-route race,
            // no dependency on a second watcher event.
            if (_unregisterWork.TryGetValue(descriptor.Route, out var work))
            {
                work.PendingReplacement = descriptor;
                Log.Modules(
                    $"[Gate] '{descriptor.Route}' rediscovered during unregister; held as pending replacement");
                return;
            }

            Refuse(descriptor.Route, RegistrationRefusal.DuplicateRoute,
                $"'{descriptor.Route}' is already registered");
            return;
        }

        // Gate 3: no eager IResidentModule attach. Runtime starts only through
        // ModuleRuntimeCoordinator.StartAsync (bridge or IRuntimeModuleLifecycle).
        // A citizen that needs a background service gets it at Start, never at
        // registration — every citizen boots Stopped.

        _registered.Add(descriptor);
        _registered.Sort(Compare);
        Log.Modules($"[Gate] registered '{descriptor.Route}'");
        RegistryChanged?.Invoke();
    }

    private void UnregisterOnMain(string route)
    {
        // Already staged (in flight or RemovalPending): a second Unregister is
        // a no-op. RetryRemoval is the explicit path for RemovalPending.
        if (_unregisterWork.ContainsKey(route))
        {
            Log.Modules($"[Gate] unregister '{route}' ignored; release already staged");
            return;
        }

        var index = _registered.FindIndex(d => Same(d.Route, route));
        if (index < 0)
        {
            // Not a failure: the searcher may see a folder vanish that never
            // registered, and a refused duplicate is deleted later like any
            // other. Recorded so it is not invisible either.
            Log.Modules($"[Gate] unregister '{route}' ignored; not registered");
            return;
        }

        // No release handler (tests / composition order): legacy immediate remove.
        if (_releaseHandler is null)
        {
            _registered.RemoveAt(index);
            Log.Modules($"[Gate] unregistered '{route}'");
            RegistryChanged?.Invoke();
            return;
        }

        var work = new UnregisterWork();
        _unregisterWork[route] = work;
        StartReleaseOnMain(route, work);
    }

    /// <summary>
    /// Invoke the bound release handler on main, observe its task, and route
    /// every result/exception back through MainQueue — never unobserved.
    /// </summary>
    private void StartReleaseOnMain(string route, UnregisterWork work)
    {
        if (work.ReleaseRunning) return;
        work.ReleaseRunning = true;
        work.Phase = UnregisterPhase.Unregistering;
        work.Error = null;
        Log.Modules($"[Gate] '{route}' unregistering; releasing runtime");

        Task<RuntimeReleaseResult> release;
        try
        {
            release = _releaseHandler!(route);
        }
        catch (Exception exception)
        {
            work.ReleaseRunning = false;
            var message = exception.GetBaseException().Message;
            _main.Post(_lifetime, () => CompleteReleaseOnMain(route, RuntimeReleaseResult.Failed(message)));
            return;
        }

        // Observe synchronously so a faulted task is never unobserved; completion
        // is posted back to the main queue (guarded by lifetime).
        release.ContinueWith(
            task =>
            {
                RuntimeReleaseResult result;
                if (task.IsFaulted)
                {
                    result = RuntimeReleaseResult.Failed(
                        task.Exception?.GetBaseException().Message ?? "Runtime release failed.");
                }
                else if (task.IsCanceled)
                {
                    result = RuntimeReleaseResult.Failed("Runtime release was cancelled.");
                }
                else
                {
                    result = task.Result;
                }

                _main.Post(_lifetime, () => CompleteReleaseOnMain(route, result));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteReleaseOnMain(string route, RuntimeReleaseResult result)
    {
        if (!_unregisterWork.TryGetValue(route, out var work)) return;
        work.ReleaseRunning = false;

        if (result.Success)
        {
            _unregisterWork.Remove(route);
            var removed = _registered.RemoveAll(d => Same(d.Route, route)) > 0;

            // Option A: register the held replacement atomically after release.
            if (work.PendingReplacement is { } replacement)
            {
                if (!_registered.Any(existing => Same(existing.Route, replacement.Route)))
                {
                    _registered.Add(replacement);
                    _registered.Sort(Compare);
                    Log.Modules(
                        $"[Gate] '{route}' re-registered from pending replacement after runtime release");
                }
                else
                {
                    // A concurrent Register beat us while release finished; drop ours.
                    Log.Modules(
                        $"[Gate] pending replacement for '{route}' dropped; route already registered");
                }
                RegistryChanged?.Invoke();
                RemovalStateChanged?.Invoke(route);
                return;
            }

            if (removed)
            {
                Log.Modules($"[Gate] unregistered '{route}'");
                RegistryChanged?.Invoke();
            }
            RemovalStateChanged?.Invoke(route);
            return;
        }

        // Failure: retain descriptor, mark RemovalPending, expose Retry removal.
        work.Phase = UnregisterPhase.RemovalPending;
        work.Error = result.Error ?? "Runtime release failed.";
        Log.Modules($"[Gate] removal of '{route}' pending: {work.Error}");
        RemovalStateChanged?.Invoke(route);
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

    private enum UnregisterPhase
    {
        Unregistering,
        RemovalPending,
    }

    /// <summary>
    /// One staged actual-unregistration. At most one pending Option A
    /// replacement per route. Descriptor stays in <c>_registered</c> until
    /// release succeeds — only successful removal drops the sidebar item.
    /// </summary>
    private sealed class UnregisterWork
    {
        public UnregisterPhase Phase { get; set; } = UnregisterPhase.Unregistering;

        public bool ReleaseRunning { get; set; }

        public string? Error { get; set; }

        public ModuleDescriptor? PendingReplacement { get; set; }
    }
}
