using Citadel.Core.Rpl;

namespace Citadel.Core.Modules;

/// <summary>
/// One Start attempt's identity. Allocated fresh for every Start (including a
/// failed one) and never reused. Distinct from the Shell operation epoch: the
/// epoch rejects stale Shell continuations; this generation fences a citizen's
/// own late worker continuations after Force Stop or a replacement runtime.
/// </summary>
public sealed record RuntimeSession(long Generation);

/// <summary>
/// Optional citizen runtime lifecycle. The shell drives this around
/// <see cref="IModule.CreateView"/>; citizens without it use the default
/// lifecycle (view lifetime is the runtime lifetime).
///
/// <see cref="IResidentModule"/> is the one-release compatibility bridge and is
/// invoked by the runtime coordinator at Start — never by ModuleGate during
/// registration.
/// </summary>
public interface IRuntimeModuleLifecycle
{
    /// <summary>
    /// Called once per successful Start attempt before CreateView. The supplied
    /// lifetime owns every runtime resource for that start; register disposal
    /// on it. Throwing fails the Start (Faulted + Retry).
    /// </summary>
    Task StartRuntimeAsync(
        Lifetime runtimeLifetime,
        RuntimeSession session,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns successfully only when it is safe to destroy the runtime
    /// lifetime. Throwing (or observing cancellation) leaves the lifetime alive
    /// and the slot Running with a stop error — nothing is force-disposed.
    /// </summary>
    Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Synchronously signal every owned process, worker, and transport to
    /// abort. Must not wait for queue drain or mutate WPF controls. Called only
    /// after the operator confirms Force Stop; the shell then destroys the
    /// runtime lifetime without awaiting settlement.
    /// </summary>
    void ForceStopRuntime(RuntimeSession session);
}
