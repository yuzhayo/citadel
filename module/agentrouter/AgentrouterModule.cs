using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Module.Agentrouter;

/// <summary>
/// The only face Agentrouter shows the shell. Route must equal `module.json`'s
/// route — the searcher checks, and the loader compares them with an ordinal,
/// case-sensitive comparison.
///
/// The lifetime is supplied, not owned: everything the view starts is registered
/// there, and navigating away destroys it.
/// </summary>
public sealed class AgentrouterModule : IModule
{
    public string Route => "agentrouter";

    public FrameworkElement CreateView(Lifetime lifetime) => new AgentrouterView(lifetime);
}
