using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citizen.PrivateDependency;

namespace Citadel.Uia.Citizen;

/// <summary>
/// A citizen whose view cannot render without its private dependency. Calling
/// into it from <c>CreateView</c> is the point: if
/// <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> fails to find
/// the assembly, this throws and the folder fails — so the test cannot pass
/// vacuously.
/// </summary>
public sealed class Dependent : IModule
{
    public string Route => "dependent";

    public FrameworkElement CreateView(Lifetime lifetime) =>
        new TextBlock { Text = Greeting.Say() };
}

/// <summary>
/// Route comes from the type name, so a distinctly-routed citizen is one short
/// subclass. <c>IModule.Route</c> is a property and the searcher checks it equals
/// the manifest route — which is why a test cannot simply rewrite `module.json`
/// and reuse one type.
/// </summary>
public abstract class NamedRouteModule : IModule
{
    public string Route => GetType().Name.ToLowerInvariant();

    public FrameworkElement CreateView(Lifetime lifetime) =>
        new TextBlock { Text = Route };
}

/// <summary>Two folders claim this one route, for the ownership cases.</summary>
public sealed class Shared : NamedRouteModule { }

/// <summary>Rename: `first` becomes `second`.</summary>
public sealed class First : NamedRouteModule { }

public sealed class Second : NamedRouteModule { }

/// <summary>Re-created with a different route: `alpha` becomes `beta`.</summary>
public sealed class Alpha : NamedRouteModule { }

public sealed class Beta : NamedRouteModule { }

/// <summary>Half-copied, then completed.</summary>
public sealed class Arriving : NamedRouteModule { }

// Twenty at once. Written out rather than generated: the searcher must be
// exercised with real distinct routes, and a fixture that lies about its route
// would prove nothing.
public sealed class Screen01 : NamedRouteModule { }

public sealed class Screen02 : NamedRouteModule { }

public sealed class Screen03 : NamedRouteModule { }

public sealed class Screen04 : NamedRouteModule { }

public sealed class Screen05 : NamedRouteModule { }

public sealed class Screen06 : NamedRouteModule { }

public sealed class Screen07 : NamedRouteModule { }

public sealed class Screen08 : NamedRouteModule { }

public sealed class Screen09 : NamedRouteModule { }

public sealed class Screen10 : NamedRouteModule { }

public sealed class Screen11 : NamedRouteModule { }

public sealed class Screen12 : NamedRouteModule { }

public sealed class Screen13 : NamedRouteModule { }

public sealed class Screen14 : NamedRouteModule { }

public sealed class Screen15 : NamedRouteModule { }

public sealed class Screen16 : NamedRouteModule { }

public sealed class Screen17 : NamedRouteModule { }

public sealed class Screen18 : NamedRouteModule { }

public sealed class Screen19 : NamedRouteModule { }

public sealed class Screen20 : NamedRouteModule { }

/// <summary>A constructor that throws, for the failure hunt.</summary>
public sealed class Throwing : IModule
{
    public Throwing() => throw new InvalidOperationException("citizen constructor failed on purpose");

    public string Route => "throwing";

    public FrameworkElement CreateView(Lifetime lifetime) => new TextBlock();
}
