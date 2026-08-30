using System.Windows;
using Citadel.Core.Rpl;

namespace Citadel.Core.Modules;

/// <summary>
/// Verbatim from v0 — do not alter. Two members, and the
/// view's lifetime is supplied by the shell rather than owned by the module.
///
/// Existing citizens compile against this unchanged, which is why the namespace
/// is Citadel.Core.Modules even though the assembly is Citadel.Contract.
/// </summary>
public interface IModule
{
    string Route { get; }

    FrameworkElement CreateView(Lifetime lifetime);
}
