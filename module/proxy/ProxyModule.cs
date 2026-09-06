using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Module.Proxy;

/// <summary>The shell entry point for the Proxy citizen.</summary>
public sealed class ProxyModule : IModule
{
    public string Route => "proxy";

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        return new ProxyView();
    }
}
