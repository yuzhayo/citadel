using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Module.Blank;

/// <summary>
/// The only face a screen shows the shell. Route must equal `module.json`'s
/// route — the searcher checks, because otherwise the sidebar entry and the
/// router disagree.
///
/// The lifetime is supplied, not owned: everything the view starts is registered
/// there, and navigating away destroys it.
/// </summary>
public sealed class BlankModule : IModule
{
    public string Route => "blank";

    public FrameworkElement CreateView(Lifetime lifetime) => new BlankView();
}
