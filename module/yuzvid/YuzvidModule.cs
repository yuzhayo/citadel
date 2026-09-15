using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Module.Yuzvid;

/// <summary>Shell entry point for the independent Yuzvid citizen.</summary>
public sealed class YuzvidModule : IModule
{
    public string Route => "yuzvid";

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        return new YuzvidView(lifetime);
    }
}
