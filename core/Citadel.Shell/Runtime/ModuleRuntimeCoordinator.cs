using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;

namespace Citadel.Shell;

/// <summary>
/// Generic per-route runtime orchestration: Start/Stop/ForceStop transactions,
/// session generation, operation epoch, and visual handoff. It never inspects
/// concrete module types or queue states.
///
/// Thread affinity: CreateView, LayoutApplier.Attach, mount/unmount,
/// IRetainedViewModule callbacks, and Lifetime.Destroy for view work run only
/// on the Shell UI thread. StartRuntimeAsync/StopRuntimeAsync are entered from
/// that thread; after awaiting background work the coordinator marshals back
/// (ConfigureAwait(true)) before mutating slot state. EnsureUiThread rejects
/// off-thread entry when an Application dispatcher exists.
///
/// Staged unregister: App binds ReleaseRouteAsync to the Gate before the
/// Searcher starts; the Gate fires RemovalStateChanged on release outcomes and
/// the coordinator re-raises it as RuntimePresentationChanged for hosts.
/// </summary>
public sealed class ModuleRuntimeCoordinator
{
    private readonly ModuleGate _gate;
    private readonly Tokens _tokens;
    private readonly Dictionary<string, ModuleRuntimeSlot> _slots = new(StringComparer.Ordinal);
    private readonly object _slotsGate = new();

    public ModuleRuntimeCoordinator(ModuleGate gate, Tokens tokens)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _gate.RemovalStateChanged += OnRemovalStateChanged;
    }

    private void OnRemovalStateChanged(string route) =>
        RuntimePresentationChanged?.Invoke(route);

    /// <summary>
    /// Raised after a slot's presentation-relevant state changed. Hosts
    /// re-marshals to the UI thread; MainWindow refreshes the content header.
    /// </summary>
    public event Action<string>? RuntimePresentationChanged;

    public ModuleRuntimeState StateOf(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return SlotOrNull(route)?.SnapshotState() ?? ModuleRuntimeState.Stopped;
    }

    internal ModuleRuntimeSlot? SlotOrNull(string route)
    {
        lock (_slotsGate) return _slots.GetValueOrDefault(route);
    }

    internal bool IsRemovalPending(string route) => _gate.IsRemovalPending(route);

    internal string? RemovalError(string route) => _gate.RemovalError(route);

    internal ModuleDescriptor? DescriptorOf(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return ResolveRegistered(route);
    }

    internal ModuleRuntimeSlot SlotFor(ModuleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_slotsGate)
        {
            if (!_slots.TryGetValue(descriptor.Route, out var slot))
            {
                slot = new ModuleRuntimeSlot(descriptor.Route);
                slot.StateChanged += () => RuntimePresentationChanged?.Invoke(descriptor.Route);
                _slots[descriptor.Route] = slot;
            }
            if (slot.Descriptor is null || !ReferenceEquals(slot.Descriptor.Instance, descriptor.Instance))
            {
                slot.AttachDescriptor(descriptor);
            }
            return slot;
        }
    }

    /// <summary>
    /// Start transaction. On failure the descriptor is retained, the new
    /// lifetime is destroyed, and the slot is Faulted — never RejectForFailedView.
    /// </summary>
    public async Task StartAsync(string route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        EnsureUiThread();

        // Staged actual unregister (Unregistering or RemovalPending): no new Start.
        if (_gate.IsStartBlocked(route))
        {
            throw new InvalidOperationException(
                $"Route '{route}' is unregistering and cannot start.");
        }

        var descriptor = ResolveRegistered(route)
            ?? throw new InvalidOperationException($"Route '{route}' is not registered.");

        var slot = SlotFor(descriptor);
        await slot.StateGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        ModuleRuntimeSlot.StartAttempt attempt;
        try
        {
            attempt = slot.BeginStart();
        }
        finally
        {
            slot.StateGate.Release();
        }

        var epoch = attempt.Epoch;
        try
        {
            // 1) Lifecycle / resident bridge BEFORE CreateView.
            if (descriptor.Instance is IRuntimeModuleLifecycle lifecycle)
            {
                await lifecycle.StartRuntimeAsync(
                    attempt.Lifetime,
                    attempt.Session,
                    cancellationToken).ConfigureAwait(true);
            }
            else if (descriptor.Instance is IResidentModule resident)
            {
                // Compatibility bridge: runtime lifetime, not application lifetime,
                // and never invoked from ModuleGate registration.
                resident.AttachApplicationLifetime(attempt.Lifetime);
            }

            ThrowIfStale(slot, epoch);
            EnsureUiThread();

            // 2) Presentation root on the UI thread (caller-entered).
            var view = descriptor.Instance.CreateView(attempt.Lifetime)
                ?? throw new InvalidOperationException("CreateView returned null");
            if (VisualParent(view) is not null)
            {
                throw new InvalidOperationException(
                    "CreateView returned a view that already has a parent");
            }

            LayoutApplier.Attach(view, descriptor.Route, descriptor.Layout, _tokens, attempt.Lifetime);

            await slot.StateGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (slot.CurrentEpoch() != epoch)
                {
                    // Force Stop (or a newer attempt) won while we created the view.
                    attempt.Lifetime.Destroy();
                    view = null!;
                    return;
                }
                slot.CompleteStart(attempt, view);
            }
            finally
            {
                slot.StateGate.Release();
            }

            // PendingStop (shutdown requested while Starting) runs after success.
            if (slot.ConsumePendingStop() && slot.SnapshotState() == ModuleRuntimeState.Running)
            {
                await StopAsync(route, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            var error = exception.GetBaseException().Message;
            await slot.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                slot.FailStart(attempt, error);
            }
            finally
            {
                slot.StateGate.Release();
            }
            // FailStart already destroyed attempt.Lifetime once under the epoch
            // check; Lifetime.Destroy is idempotent — safe if both paths ran.
            attempt.Lifetime.Destroy();
            throw;
        }
    }

    /// <summary>
    /// Safe Stop. Success unmounts the runtime view, destroys the runtime
    /// lifetime; failure retains everything and returns the slot to Running
    /// with a stop error.
    /// </summary>
    public async Task StopAsync(string route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        EnsureUiThread();

        var slot = SlotOrNull(route)
            ?? throw new InvalidOperationException($"No runtime slot for '{route}'.");

        long epoch;
        CancellationToken stopToken;
        await slot.StateGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            // Shutdown Stop during Starting is recorded, not run concurrently.
            if (slot.SnapshotState() == ModuleRuntimeState.Starting)
            {
                slot.RequestPendingStop();
                return;
            }
            slot.BeginStop();
            epoch = slot.CurrentEpoch();
            stopToken = slot.StopToken();
        }
        finally
        {
            slot.StateGate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stopToken, cancellationToken);

        try
        {
            var descriptor = slot.Descriptor
                ?? throw new InvalidOperationException($"No descriptor for '{route}'.");

            if (descriptor.Instance is IRuntimeModuleLifecycle lifecycle)
            {
                await lifecycle.StopRuntimeAsync(
                    new RuntimeSession(slot.Generation),
                    linked.Token).ConfigureAwait(true);
            }

            EnsureUiThread();
            if (slot.CurrentEpoch() != epoch)
            {
                // Force Stop preempted this attempt; do not touch the slot.
                return;
            }

            // Unmount first while RuntimeView is still set, then destroy.
            UnmountRuntimeView(slot);

            await slot.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                slot.CompleteStop(epoch);
            }
            finally
            {
                slot.StateGate.Release();
            }
        }
        catch (Exception exception)
        {
            EnsureUiThread();
            if (slot.CurrentEpoch() != epoch)
            {
                return;
            }
            var error = exception.GetBaseException().Message;
            await slot.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                slot.FailStop(epoch, error);
            }
            finally
            {
                slot.StateGate.Release();
            }
            throw;
        }
    }

    /// <summary>
    /// Operator Force Stop. Allowed only from Stopping, or Running with a
    /// recorded Stop error. Preempts a stuck normal Stop: bumps the epoch,
    /// cancels the in-flight Stop, signals ForceStopRuntime, unmounts, and
    /// destroys without awaiting drain. Never used by Exit or automatic paths.
    /// </summary>
    public async Task ForceStopAsync(string route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        EnsureUiThread();

        var slot = SlotOrNull(route)
            ?? throw new InvalidOperationException($"No runtime slot for '{route}'.");

        long epoch;
        await slot.StateGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var state = slot.SnapshotState();
            var hasStopError = !string.IsNullOrEmpty(slot.LastError);
            var allowed = state == ModuleRuntimeState.Stopping
                || (state == ModuleRuntimeState.Running && hasStopError);
            if (!allowed)
            {
                throw new InvalidOperationException(
                    $"Force Stop is not allowed from {state} on '{route}'.");
            }
            epoch = slot.BeginForceStopPreempt();
        }
        finally
        {
            slot.StateGate.Release();
        }

        try
        {
            if (slot.Descriptor?.Instance is IRuntimeModuleLifecycle lifecycle)
            {
                lifecycle.ForceStopRuntime(new RuntimeSession(slot.Generation));
            }

            EnsureUiThread();
            UnmountRuntimeView(slot);

            await slot.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                if (slot.CurrentEpoch() == epoch)
                {
                    slot.CompleteForceStop();
                }
            }
            finally
            {
                slot.StateGate.Release();
            }
        }
        catch (Exception exception)
        {
            var error = exception.GetBaseException().Message;
            await slot.StateGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                slot.FailForceStop(epoch, error);
            }
            finally
            {
                slot.StateGate.Release();
            }
            throw;
        }
    }

    /// <summary>
    /// Retry a failed actual-unregistration release (RemovalPending). Posts the
    /// Gate retry and completes when RemovalStateChanged fires for this route
    /// (success clears the work; a repeated failure re-arms Retry chrome).
    /// </summary>
    internal Task RetryRemovalAsync(string route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        EnsureUiThread();

        if (!_gate.IsRemovalPending(route))
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void HandlerAny(string changed)
        {
            if (!string.Equals(changed, route, StringComparison.Ordinal)) return;
            _gate.RemovalStateChanged -= HandlerAny;
            tcs.TrySetResult();
        }

        _gate.RemovalStateChanged += HandlerAny;
        _gate.RetryRemoval(route);

        return tcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Release a route for actual unregistration: safe Stop only. Never Force.
    /// Joins an in-flight Stop/ForceStop under the state gate — never starts a
    /// second concurrent Stop. On success the slot is Stopped/Faulted; on
    /// failure the error is returned for the Gate's RemovalPending path.
    /// </summary>
    public async Task<RuntimeReleaseResult> ReleaseRouteAsync(
        string route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        var slot = SlotOrNull(route);
        // Faulted already destroyed its runtime lifetime on Start failure —
        // nothing to stop, and BeginStop would illegally reject that state.
        // No slot: never started (or already torn down).
        if (slot is null || slot.SnapshotState()
            is ModuleRuntimeState.Stopped or ModuleRuntimeState.Faulted)
        {
            return RuntimeReleaseResult.Released;
        }

        try
        {
            var state = slot.SnapshotState();
            if (state is ModuleRuntimeState.Stopping or ModuleRuntimeState.ForceStopping)
            {
                // Join the in-flight Stop — Release during Stopping never starts
                // a second concurrent one.
                await WaitForExitIdleAsync(slot, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                // Running: safe Stop. Starting: records PendingStop; Start's
                // continuation settles first, then we wait for exit idle.
                await StopAsync(route, cancellationToken).ConfigureAwait(true);
                await WaitForExitIdleAsync(slot, cancellationToken).ConfigureAwait(true);
            }

            var final = slot.SnapshotState();
            if (final is ModuleRuntimeState.Stopped or ModuleRuntimeState.Faulted)
            {
                return RuntimeReleaseResult.Released;
            }

            var error = string.IsNullOrEmpty(slot.LastError)
                ? final.ToString()
                : slot.LastError;
            return RuntimeReleaseResult.Failed(error ?? "Runtime release did not stop.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RuntimeReleaseResult.Failed("Runtime release was cancelled.");
        }
        catch (Exception exception)
        {
            return RuntimeReleaseResult.Failed(exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Safe Stop every live slot in parallel (slots are independent). Routes
    /// already Stopped/Faulted are skipped. Returns one
    /// <c>"route: error"</c> entry per failure; successful stops stay stopped —
    /// Stop is not transactional across modules. Never uses Force Stop.
    /// </summary>
    public async Task<IReadOnlyList<string>> StopAllAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureUiThread();

        List<ModuleRuntimeSlot> slots;
        lock (_slotsGate)
        {
            slots = [.. _slots.Values];
        }

        var failures = new List<string>();
        var failureGate = new object();
        await Task.WhenAll(slots.Select(slot => StopAllOneAsync(slot, failures, failureGate, cancellationToken)))
            .ConfigureAwait(true);

        lock (failureGate)
        {
            return [.. failures];
        }
    }

    private async Task StopAllOneAsync(
        ModuleRuntimeSlot slot,
        List<string> failures,
        object failureGate,
        CancellationToken cancellationToken)
    {
        var route = slot.Route;
        try
        {
            var state = slot.SnapshotState();
            if (state is ModuleRuntimeState.Stopped or ModuleRuntimeState.Faulted)
            {
                return;
            }

            if (state is ModuleRuntimeState.Stopping or ModuleRuntimeState.ForceStopping)
            {
                // Join the in-flight Stop/Force — never start a second concurrent one.
                await WaitForExitIdleAsync(slot, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                // Running: safe Stop. Starting: records PendingStop; Start's
                // continuation (or FailStart) settles first, then we wait.
                await StopAsync(route, cancellationToken).ConfigureAwait(true);
                await WaitForExitIdleAsync(slot, cancellationToken).ConfigureAwait(true);
            }

            var final = slot.SnapshotState();
            if (final is ModuleRuntimeState.Stopped or ModuleRuntimeState.Faulted)
            {
                return;
            }

            // Running-with-error after a failed Stop, or an unexpected residue.
            var error = string.IsNullOrEmpty(slot.LastError)
                ? final.ToString()
                : slot.LastError;
            lock (failureGate)
            {
                failures.Add($"{route}: {error}");
            }
        }
        catch (Exception exception)
        {
            lock (failureGate)
            {
                failures.Add($"{route}: {exception.GetBaseException().Message}");
            }
        }
    }

    /// <summary>
    /// Wait until the slot leaves Starting/Stopping/ForceStopping (and any
    /// PendingStop chain) for Stopped/Faulted, or settles as Running-with-error
    /// after a failed Stop. Driven by StateChanged — no polling on the UI thread.
    /// </summary>
    private async Task WaitForExitIdleAsync(
        ModuleRuntimeSlot slot,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = slot.SnapshotState();
            if (state is ModuleRuntimeState.Stopped or ModuleRuntimeState.Faulted)
            {
                return;
            }

            if (state == ModuleRuntimeState.Running)
            {
                if (!string.IsNullOrEmpty(slot.LastError))
                {
                    // FailStop returned the slot to Running with the error.
                    return;
                }

                if (!slot.PendingStop)
                {
                    // Start finished without a recorded stop — issue Stop once.
                    // A failure here is reported via LastError / final state on
                    // the next loop iteration (FailStop sets Running+error).
                    try
                    {
                        await StopAsync(slot.Route, cancellationToken).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        return;
                    }

                    continue;
                }
            }

            await WaitForStateChangeAsync(slot, cancellationToken).ConfigureAwait(true);
        }
    }

    private static Task WaitForStateChangeAsync(
        ModuleRuntimeSlot slot,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler()
        {
            slot.StateChanged -= Handler;
            tcs.TrySetResult();
        }

        slot.StateChanged += Handler;
        var registration = cancellationToken.Register(() =>
        {
            slot.StateChanged -= Handler;
            tcs.TrySetCanceled(cancellationToken);
        });
        return tcs.Task.ContinueWith(
            task =>
            {
                registration.Dispose();
                return task;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// UI-thread only. Removes the runtime view from its surface and, only when
    /// currently mounted, invokes IRetainedViewModule.OnViewDetached first.
    /// Navigation, safe Stop, Force Stop, Start rollback, unregister, and host
    /// disposal all use this one operation — a view already detached by
    /// navigation is never sent a duplicate detach.
    /// </summary>
    internal void UnmountRuntimeView(string route)
    {
        var slot = SlotOrNull(route);
        if (slot is null) return;
        UnmountRuntimeView(slot);
    }

    internal void UnmountRuntimeView(ModuleRuntimeSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        EnsureUiThread();

        var view = slot.RuntimeView;
        if (view is null)
        {
            slot.MarkViewUnmounted();
            return;
        }

        if (slot.IsViewMounted() && view is IRetainedViewModule retained)
        {
            retained.OnViewDetached();
        }

        ClearVisualParent(view);
        slot.MarkViewUnmounted();
    }

    /// <summary>
    /// UI-thread only. Puts the runtime view into the host surface and fires
    /// OnViewAttached once per real mount. Idempotent when already mounted here.
    /// </summary>
    internal void MountRuntimeView(string route, ContentControl surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var slot = SlotOrNull(route)
            ?? throw new InvalidOperationException($"No runtime slot for '{route}'.");
        MountRuntimeView(slot, surface);
    }

    internal void MountRuntimeView(ModuleRuntimeSlot slot, ContentControl surface)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(surface);
        EnsureUiThread();

        var view = slot.RuntimeView;
        if (view is null) return;

        if (slot.IsViewMounted())
        {
            if (ReferenceEquals(view.Parent, surface)) return;
            // Mounted elsewhere: detach once, then re-mount here.
            UnmountRuntimeView(slot);
        }

        ClearVisualParent(view);
        surface.Content = view;
        slot.MarkViewMounted();
        if (view is IRetainedViewModule retained)
        {
            retained.OnViewAttached();
        }
    }

    private ModuleDescriptor? ResolveRegistered(string route) =>
        _gate.Snapshot()
            .FirstOrDefault(descriptor => string.Equals(descriptor.Route, route, StringComparison.Ordinal));

    private static void EnsureUiThread()
    {
        // Test harnesses construct no Application; production always has one.
        var app = System.Windows.Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Module runtime operations must run on the Shell UI thread.");
        }
    }

    private static void ThrowIfStale(ModuleRuntimeSlot slot, long epoch)
    {
        if (slot.CurrentEpoch() != epoch)
        {
            throw new OperationCanceledException(
                $"Runtime Start for '{slot.Route}' was superseded (epoch {epoch}).");
        }
    }

    private static void ClearVisualParent(FrameworkElement view)
    {
        switch (view.Parent)
        {
            case ContentControl content when ReferenceEquals(content.Content, view):
                content.Content = null;
                break;
            case System.Windows.Controls.Panel panel when panel.Children.Contains(view):
                panel.Children.Remove(view);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, view):
                decorator.Child = null;
                break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, view):
                presenter.Content = null;
                break;
        }
    }

    private static DependencyObject? VisualParent(FrameworkElement element) =>
        element.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(element);
}
