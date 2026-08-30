using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Crl;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Shell;
using Citadel.Ui.Animations;

namespace Citadel.Uia;

/// <summary>
/// WPF needs an STA thread and xUnit does not supply one. Same shape as
/// Citadel.Ui.Tests/StaTest.cs, kept local so this project stays
/// dependency-free rather than pulling in an STA-specific xUnit package.
/// </summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

/// <summary>
/// A MainQueue whose wake is manual, so a test can prove that gate mutations
/// really are deferred and then drain them deterministically.
/// </summary>
internal sealed class TestMain
{
    private readonly List<Action> _pending = [];

    public TestMain(bool isMain = true) =>
        Queue = new MainQueue(drain => _pending.Add(drain), () => isMain);

    public MainQueue Queue { get; }

    public bool HasPendingWake => _pending.Count > 0;

    /// <summary>Runs every scheduled drain, including ones a drain schedules.</summary>
    public void Pump()
    {
        for (var guard = 0; _pending.Count > 0 && guard < 100; guard++)
        {
            var batch = _pending.ToArray();
            _pending.Clear();
            foreach (var drain in batch) drain();
        }
    }
}

/// <summary>Deterministic access to AnimationManager's one shared frame clock.</summary>
internal sealed class ManualFrameClock : IFrameClock
{
    private Action<TimeSpan>? _tick;

    public bool Attached => _tick is not null;

    public void Attach(Action<TimeSpan> tick)
    {
        if (_tick is not null) throw new InvalidOperationException("frame clock already attached");
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
    }

    public void Detach() => _tick = null;

    public void Pulse(double milliseconds) =>
        (_tick ?? throw new InvalidOperationException("frame clock is not attached"))
            (TimeSpan.FromMilliseconds(milliseconds));
}

/// <summary>A citizen whose view is whatever the test needs it to be.</summary>
internal sealed class FakeModule(string route, Func<Lifetime, FrameworkElement> create) : IModule
{
    public string Route { get; } = route;

    public int CreateCount { get; private set; }

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        CreateCount++;
        return create(lifetime);
    }
}

internal static class Fake
{
    public static ModuleDescriptor Descriptor(
        string route,
        string? title = null,
        string? icon = "",
        int order = 10,
        Func<Lifetime, FrameworkElement>? create = null,
        LayoutDeclaration? layout = null) =>
        new(
            route,
            title ?? route,
            icon,
            order,
            new FakeModule(route, create ?? (_ => new Border())),
            layout);

    public static LayoutDeclaration Declaration(string json) =>
        new(JsonNode.Parse(json)!.AsObject());

    public static Tokens Store()
    {
        // System.IO.Path is qualified because WPF's Shapes.Path is in scope.
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "citadel-uia-" + Guid.NewGuid().ToString("N"));
        return new Tokens(System.IO.Path.Combine(dir, "ui.json"));
    }
}

/// <summary>
/// Stands in for the built-in settings view. A distinct type so a test can tell
/// "landed on settings" from "a citizen view is still attached" — asserting
/// against a bare Border cannot, because a fake citizen view is one too.
/// </summary>
internal sealed class FakeSettingsView : ContentControl
{
}

/// <summary>
/// Gate + Router without a window: enough of the shell to exercise every
/// registration and navigation path, and no filesystem anywhere.
/// </summary>
internal sealed class ShellHarness : IDisposable
{
    private readonly Lifetime _lifetime = new();

    public ShellHarness(bool withSettingsRoute = true)
    {
        Main = new TestMain();
        Tokens = Fake.Store();
        Gate = new ModuleGate(Main.Queue, _lifetime);
        Host = new ContentControl();
        FrameClock = new ManualFrameClock();
        Animations = new AnimationManager(FrameClock);

        var builtIn = new Dictionary<string, BuiltInRoute>(StringComparer.Ordinal);
        if (withSettingsRoute)
        {
            builtIn[Router.FallbackRoute] = new BuiltInRoute(
                "Settings",
                _ =>
                {
                    SettingsShown++;
                    return new FakeSettingsView();
                });
        }

        Router = new Router(Host, Gate, Tokens, Animations, builtIn);
        _lifetime.Add(Router.Dispose);
        Gate.RegistryChanged += () => Router.OnRegistryChanged();
    }

    public TestMain Main { get; }

    public Tokens Tokens { get; }

    public ModuleGate Gate { get; }

    public ContentControl Host { get; }

    public ManualFrameClock FrameClock { get; }

    public AnimationManager Animations { get; }

    public Router Router { get; }

    public int SettingsShown { get; private set; }

    public void Dispose()
    {
        _lifetime.Destroy();
        Animations.Dispose();
    }
}
