using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Citadel.Shell;

/// <summary>
/// Per-route runtime state. Keyed by exact route (ordinal). Holds the runtime
/// lifetime only while Starting/Running/Stopping/ForceStopping, and the runtime
/// view only after Start succeeds.
///
/// State transitions and visual ownership mutate only under the short-lived
/// per-route gate, which is never held across an awaited citizen operation.
/// </summary>
internal sealed class ModuleRuntimeSlot
{
    private readonly object _gate = new();

    public ModuleRuntimeSlot(string route)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
    }

    public string Route { get; }

    public ModuleRuntimeState State { get; private set; } = ModuleRuntimeState.Stopped;

    public ModuleDescriptor? Descriptor { get; private set; }

    /// <summary>Alive only while Starting/Running/Stopping/ForceStopping.</summary>
    public Lifetime? RuntimeLifetime { get; private set; }

    /// <summary>Citizen presentation root; set only after a successful Start.</summary>
    public FrameworkElement? RuntimeView { get; private set; }

    /// <summary>
    /// True while the runtime view is mounted in a host surface and has received
    /// a matching OnViewAttached. Cleared by UnmountRuntimeView so a second
    /// unmount never sends a duplicate OnViewDetached.
    /// </summary>
    public bool ViewMounted { get; private set; }

    public string? LastError { get; private set; }

    public bool PendingStop { get; private set; }

    /// <summary>
    /// Monotonic Start-attempt generation. Never reused, including after a
    /// failed Start. Fences the citizen's own late workers.
    /// </summary>
    public long Generation { get; private set; }

    /// <summary>
    /// Shell operation epoch. Bumped by Force Stop to invalidate an in-flight
    /// normal Start/Stop continuation before it can touch slot state.
    /// </summary>
    public long OperationEpoch { get; private set; }

    public CancellationTokenSource? ActiveStopCancellation { get; private set; }

    /// <summary>
    /// Serializes Start, Stop, Force Stop, unregister release, and exit for
    /// this route. Never held across an awaited citizen operation.
    /// </summary>
    public SemaphoreSlim StateGate { get; } = new(1, 1);

    public event Action? StateChanged;

    public ModuleRuntimeState SnapshotState()
    {
        lock (_gate) return State;
    }

    public void AttachDescriptor(ModuleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            Descriptor = descriptor;
            // Boot / first registration: always Stopped. Runtime is never
            // persisted or inferred as Running from registration alone.
            State = ModuleRuntimeState.Stopped;
            LastError = null;
            PendingStop = false;
        }
        RaiseStateChanged();
    }

    public void DetachDescriptor()
    {
        lock (_gate)
        {
            Descriptor = null;
            State = ModuleRuntimeState.Stopped;
            LastError = null;
            PendingStop = false;
            RuntimeView = null;
            ViewMounted = false;
            RuntimeLifetime = null;
            ActiveStopCancellation?.Dispose();
            ActiveStopCancellation = null;
        }
        RaiseStateChanged();
    }

    /// <summary>Mark the runtime view mounted after a real attach pair.</summary>
    public void MarkViewMounted()
    {
        lock (_gate) ViewMounted = true;
    }

    /// <summary>Mark the runtime view unmounted after a real detach pair.</summary>
    public void MarkViewUnmounted()
    {
        lock (_gate) ViewMounted = false;
    }

    public bool IsViewMounted()
    {
        lock (_gate) return ViewMounted;
    }

    /// <summary>
    /// Begin a Start attempt: bump generation + epoch, allocate lifetime, mark
    /// Starting. Caller must hold <see cref="StateGate"/>.
    /// </summary>
    public StartAttempt BeginStart()
    {
        lock (_gate)
        {
            if (State is not (ModuleRuntimeState.Stopped or ModuleRuntimeState.Faulted))
            {
                throw new InvalidOperationException(
                    $"Start is not allowed from {State} on '{Route}'.");
            }
            if (Descriptor is null)
            {
                throw new InvalidOperationException($"No descriptor for '{Route}'.");
            }

            Generation++;
            OperationEpoch++;
            var session = new RuntimeSession(Generation);
            var lifetime = new Lifetime();
            var attempt = new StartAttempt(session, lifetime, OperationEpoch, Descriptor);
            RuntimeLifetime = lifetime;
            RuntimeView = null;
            ViewMounted = false;
            LastError = null;
            PendingStop = false;
            State = ModuleRuntimeState.Starting;
            return attempt;
        }
    }

    /// <summary>Complete a successful Start. Caller holds <see cref="StateGate"/>.</summary>
    public void CompleteStart(StartAttempt attempt, FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            RequireCurrent(attempt);
            if (State != ModuleRuntimeState.Starting)
            {
                throw new InvalidOperationException(
                    $"CompleteStart requires Starting on '{Route}', was {State}.");
            }
            RuntimeView = view;
            // RuntimeLifetime already set by BeginStart.
            State = ModuleRuntimeState.Running;
            LastError = null;
        }
        RaiseStateChanged();
    }

    /// <summary>Fail a Start: destroy lifetime, keep descriptor, mark Faulted.</summary>
    public void FailStart(StartAttempt attempt, string error)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            // Stale epoch: Force Stop (or a newer attempt) already owns this
            // slot — only tear down the orphaned attempt lifetime below.
            if (attempt.Epoch == OperationEpoch)
            {
                if (State == ModuleRuntimeState.Starting)
                {
                    State = ModuleRuntimeState.Faulted;
                    LastError = error;
                }
                RuntimeView = null;
                ViewMounted = false;
                RuntimeLifetime = null;
            }
        }
        // Destroy outside the lock: cleanups may take locks of their own.
        // Lifetime.Destroy is idempotent, so a stale-epoch path that already
        // tore down elsewhere is safe.
        attempt.Lifetime.Destroy();
        RaiseStateChanged();
    }

    public void BeginStop()
    {
        lock (_gate)
        {
            if (State != ModuleRuntimeState.Running)
            {
                throw new InvalidOperationException(
                    $"Stop is not allowed from {State} on '{Route}'.");
            }
            State = ModuleRuntimeState.Stopping;
            ActiveStopCancellation = new CancellationTokenSource();
        }
        RaiseStateChanged();
    }

    public long CurrentEpoch()
    {
        lock (_gate) return OperationEpoch;
    }

    public CancellationToken StopToken()
    {
        lock (_gate)
        {
            return ActiveStopCancellation?.Token ?? CancellationToken.None;
        }
    }

    /// <summary>Successful Stop: clear runtime, mark Stopped. Caller holds StateGate.</summary>
    public void CompleteStop(long epoch)
    {
        Lifetime? lifetime;
        lock (_gate)
        {
            if (epoch != OperationEpoch || State != ModuleRuntimeState.Stopping)
            {
                return;
            }
            lifetime = RuntimeLifetime;
            RuntimeLifetime = null;
            RuntimeView = null;
            ViewMounted = false;
            ActiveStopCancellation?.Dispose();
            ActiveStopCancellation = null;
            LastError = null;
            PendingStop = false;
            State = ModuleRuntimeState.Stopped;
        }
        lifetime?.Destroy();
        RaiseStateChanged();
    }

    /// <summary>Failed Stop: back to Running with error; resources retained.</summary>
    public void FailStop(long epoch, string error)
    {
        lock (_gate)
        {
            if (epoch != OperationEpoch || State != ModuleRuntimeState.Stopping)
            {
                return;
            }
            State = ModuleRuntimeState.Running;
            LastError = error;
            ActiveStopCancellation?.Dispose();
            ActiveStopCancellation = null;
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Force Stop preemption: bump epoch, cancel in-flight normal Stop, return
    /// the cancelled source's token owner epoch. Caller holds StateGate.
    /// </summary>
    public long BeginForceStopPreempt()
    {
        lock (_gate)
        {
            OperationEpoch++;
            ActiveStopCancellation?.Cancel();
            ActiveStopCancellation?.Dispose();
            ActiveStopCancellation = null;
            State = ModuleRuntimeState.ForceStopping;
            return OperationEpoch;
        }
    }

    public void CompleteForceStop()
    {
        Lifetime? lifetime;
        lock (_gate)
        {
            lifetime = RuntimeLifetime;
            RuntimeLifetime = null;
            RuntimeView = null;
            ViewMounted = false;
            LastError = null;
            PendingStop = false;
            State = ModuleRuntimeState.Stopped;
        }
        lifetime?.Destroy();
        RaiseStateChanged();
    }

    /// <summary>
    /// Force Stop abort itself threw: back to Running with a force-stop error;
    /// runtime retained — never destroy on a failed abort signal.
    /// </summary>
    public void FailForceStop(long epoch, string error)
    {
        lock (_gate)
        {
            if (epoch != OperationEpoch || State != ModuleRuntimeState.ForceStopping)
            {
                return;
            }
            State = ModuleRuntimeState.Running;
            LastError = error;
        }
        RaiseStateChanged();
    }

    public void RequestPendingStop()
    {
        lock (_gate) PendingStop = true;
    }

    public bool ConsumePendingStop()
    {
        lock (_gate)
        {
            if (!PendingStop) return false;
            PendingStop = false;
            return true;
        }
    }

    private void RequireCurrent(StartAttempt attempt)
    {
        if (attempt.Epoch != OperationEpoch)
        {
            throw new InvalidOperationException(
                $"Stale Start epoch for '{Route}' (have {attempt.Epoch}, want {OperationEpoch}).");
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    /// <summary>One Start attempt's identity and allocated resources.</summary>
    public sealed record StartAttempt(
        RuntimeSession Session,
        Lifetime Lifetime,
        long Epoch,
        ModuleDescriptor Descriptor);
}
