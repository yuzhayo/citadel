using Citadel.Core.Rpl;

namespace Citadel.Core.Modules;

/// <summary>
/// Optional contract for a citizen with work that must survive routed-view
/// destruction.
///
/// Compatibility bridge (one release): the shell supplies the real runtime
/// lifetime once from the runtime coordinator at Start — never from
/// ModuleGate during registration, and never the application lifetime. A view
/// lifetime must never be attached here. New citizens should implement
/// <see cref="IRuntimeModuleLifecycle"/> instead; this interface remains only
/// so a dynamically discovered resident citizen keeps working across one
/// release after eager registration attach is removed.
/// </summary>
public interface IResidentModule
{
    void AttachApplicationLifetime(Lifetime applicationLifetime);
}
