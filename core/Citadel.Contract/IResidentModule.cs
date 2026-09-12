using Citadel.Core.Rpl;

namespace Citadel.Core.Modules;

/// <summary>
/// Optional contract for a citizen with work that must survive routed-view
/// destruction. The shell supplies the real application lifetime once, after
/// registration succeeds. A view lifetime must never be attached here.
/// </summary>
public interface IResidentModule
{
    void AttachApplicationLifetime(Lifetime applicationLifetime);
}
