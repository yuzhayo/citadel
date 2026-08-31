using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Module.Camoprof;

/// <summary>
/// The only face this screen shows the shell. Route must equal module.json's
/// route — the searcher checks. The lifetime is supplied, not owned: the view
/// registers its pyhost cleanup there, and navigating away destroys it.
/// </summary>
public sealed class CamoprofModule : IModule
{
    public string Route => "camoprof";

    public FrameworkElement CreateView(Lifetime lifetime) => new CamoprofView(lifetime);
}
