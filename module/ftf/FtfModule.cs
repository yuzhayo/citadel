using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Module.FTF;

/// <summary>The shell entry point for the FTF citizen.</summary>
public sealed class FtfModule : IModule
{
    public string Route => "ftf";

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        return new FtfView();
    }
}
