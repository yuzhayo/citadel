using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Crl;
using Citadel.Core.Rpl;
using Citadel.Shell;

namespace Citadel.Uia;

/// <summary>
/// Router navigation and CreateView-failure isolation. Every assertion checks
/// both halves: where the router
/// landed *and* whether the view's lifetime actually died. Half of this passing
/// would be the real defect — a sidebar entry gone with a timer
/// still ticking.
/// </summary>
[Collection("Shell power saving serial")]
public class RouterTests
{
    [Fact]
    public void Navigate_ToCitizen_OwnsTheViewLifetime()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();

            shell.Router.Navigate("alpha");

            Assert.Equal("alpha", shell.Router.CurrentRoute);
            Assert.NotNull(shell.Router.ViewLifetime);
            Assert.True(shell.Router.ViewLifetime!.Alive);
        });
    }

    [Fact]
    public void NavigateAway_RetainsTheHostLifetime_AndUnmountsRuntime()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();

            shell.Router.Navigate("alpha");
            var hostLifetime = shell.Router.ViewLifetime!;
            Assert.IsType<ModuleRuntimeHost>(shell.Router.CurrentView);

            shell.Router.Navigate(Router.FallbackRoute);

            // Gate 3: the stable host is retained across navigation, not destroyed.
            Assert.True(hostLifetime.Alive);
            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);
            Assert.IsType<FakeSettingsView>(shell.Router.CurrentView);
            Assert.NotSame(hostLifetime, shell.Router.ViewLifetime);
        });
    }

    /// <summary>
    /// Nothing is left displayed, so nothing owns a lifetime. Pins that
    /// DestroyView really nulls the field rather than leaving a dead one —
    /// v0 destroyed without nulling (MainWindow.xaml.cs:208).
    /// </summary>
    [Fact]
    public void NoDisplayedView_MeansNoViewLifetime()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness(withSettingsRoute: false);
            Assert.Null(shell.Router.ViewLifetime);

            shell.Router.Navigate("nope");

            Assert.Null(shell.Router.ViewLifetime);
        });
    }

    [Fact]
    public void Unregister_DisplayedRoute_LandsOnSettings_AndDestroysTheHost()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();

            shell.Router.Navigate("alpha");
            var hostLifetime = shell.Router.ViewLifetime!;

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();

            // Host is destroyed when the route leaves the registry — not on
            // mere navigation (that path retains).
            Assert.False(hostLifetime.Alive);
            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);
            Assert.NotSame(hostLifetime, shell.Router.ViewLifetime);
            Assert.True(shell.Router.ViewLifetime!.Alive);
        });
    }

    /// <summary>
    /// Unregistering something else must not disturb a healthy view — the
    /// registry changes far more often than the displayed route does.
    /// </summary>
    [Fact]
    public void Unregister_ADifferentRoute_LeavesTheDisplayedViewAlone()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Gate.Register(Fake.Descriptor("beta"));
            shell.Main.Pump();

            shell.Router.Navigate("alpha");
            var lifetime = shell.Router.ViewLifetime;

            shell.Gate.Unregister("beta");
            shell.Main.Pump();

            Assert.Same(lifetime, shell.Router.ViewLifetime);
            Assert.True(lifetime!.Alive);
            Assert.Equal("alpha", shell.Router.CurrentRoute);
        });
    }

    [Fact]
    public void UnknownRoute_FallsBackToSettings()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Router.Navigate("nope");

            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);
        });
    }

    [Fact]
    public void CreateViewFailure_OnlyOnStart_FaultsAndKeepsTheCitizen_Throws()
        => CreateViewFailure_FaultsMode("throws");

    [Fact]
    public void CreateViewFailure_OnlyOnStart_FaultsAndKeepsTheCitizen_Null()
        => CreateViewFailure_FaultsMode("null");

    [Fact]
    public void CreateViewFailure_OnlyOnStart_FaultsAndKeepsTheCitizen_Parented()
        => CreateViewFailure_FaultsMode("parented");

    private static void CreateViewFailure_FaultsMode(string mode)
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            var lifetimeDied = false;

            Func<Lifetime, FrameworkElement> create = mode switch
            {
                "throws" => _ => throw new InvalidOperationException("boom"),
                "null" => _ => null!,
                _ => lifetime =>
                {
                    var child = new Border();
                    _ = new ContentControl { Content = child }; // already parented
                    lifetime.Add(() => lifetimeDied = true);
                    return child;
                },
            };

            shell.Gate.Register(Fake.Descriptor("broken", create: create));
            shell.Main.Pump();

            shell.Router.Navigate("broken");
            Assert.Equal("broken", shell.Router.CurrentRoute);
            Assert.IsType<ModuleRuntimeHost>(shell.Router.CurrentView);

            Run(() => shell.Coordinator.StartAsync("broken").ContinueWith(_ =>
            {
                Assert.Equal(ModuleRuntimeState.Faulted,
                    shell.Coordinator.StateOf("broken"));
                Assert.Single(shell.Gate.Snapshot());
                Assert.Empty(shell.Gate.Failures());
                Assert.IsType<ModuleRuntimeHost>(shell.Router.CurrentView);
                if (mode == "parented") Assert.True(lifetimeDied);
            }, TaskContinuationOptions.ExecuteSynchronously));
        });
    }

    private static void Run(Func<Task> body) =>
        body().GetAwaiter().GetResult();

    /// <summary>
    /// Gate 3: re-navigating the same active citizen is idempotent — the stable
    /// host stays put and CreateView is never re-entered (it only runs on Start).
    /// </summary>
    [Fact]
    public void Navigate_ToTheSameRouteAgain_IsIdempotent_WithoutRecreatingTheHost()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            var descriptor = Fake.Descriptor("alpha");
            shell.Gate.Register(descriptor);
            shell.Main.Pump();

            shell.Router.Navigate("alpha");
            var first = shell.Router.ViewLifetime;
            var firstView = shell.Router.CurrentView;
            shell.Router.Navigate("alpha");

            Assert.Same(first, shell.Router.ViewLifetime);
            Assert.Same(firstView, shell.Router.CurrentView);
            Assert.True(first!.Alive);
            Assert.Equal(0, ((FakeModule)descriptor.Instance).CreateCount);
        });
    }

    /// <summary>
    /// Only reachable if the composition root forgot the built-in factory. It
    /// must degrade to an empty host rather than recursing on fallback.
    /// </summary>
    [Fact]
    public void MissingSettingsFactory_LeavesAnEmptyHost_WithoutRecursing()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness(withSettingsRoute: false);

            shell.Router.Navigate("nope");

            Assert.Null(shell.Router.CurrentRoute);
            Assert.Null(shell.Router.CurrentView);
            Assert.Empty(Assert.IsType<Grid>(shell.Host.Content).Children);
        });
    }

    /// <summary>
    /// A built-in screen owns a Lifetime too. Appearance edits
    /// tokens and subscribes to TokensChanged; without one it would have nothing
    /// to unsubscribe on, and only citizens would be leak-proof.
    /// </summary>
    [Fact]
    public void BuiltInRoute_AlsoGetsAnOwnedLifetime()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Router.Navigate(Router.FallbackRoute);
            var settingsLifetime = shell.Router.ViewLifetime;

            Assert.NotNull(settingsLifetime);
            Assert.True(settingsLifetime!.Alive);

            shell.Router.Navigate(Router.FallbackRoute);

            Assert.False(settingsLifetime.Alive);
        });
    }

/// <summary>
    /// Gate 3: same-route citizen Navigate is idempotent (host stays). CreateView
    /// only fails on Start, which never unregisters — so this pins that a
    /// re-navigate does NOT fall through to Settings and does NOT re-enter create.
    /// </summary>
    [Fact]
    public void SameRouteRenavigate_DoesNotRebuildOrFallThrough()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            var fail = false;
            shell.Gate.Register(Fake.Descriptor("alpha", create: _ =>
                fail ? throw new InvalidOperationException("boom") : new Border()));
            shell.Main.Pump();

            shell.Router.Navigate("alpha");
            var navigations = new List<string>();
            shell.Router.Navigated += navigations.Add;

            fail = true;
            shell.Router.Navigate("alpha");
            shell.Main.Pump();

            // Idempotent: no second Navigated, still on alpha, Settings not built.
            Assert.Empty(navigations);
            Assert.Equal("alpha", shell.Router.CurrentRoute);
            Assert.Equal(0, shell.SettingsShown); // ShellHarness does not auto-navigate
        });
    }

    [Fact]
    public void Navigation_CrossfadesOnTheSharedClock_ThenReleasesTheOldLayer()
    {
        Sta.Run(() =>
        {
            PowerSaving.Set(false);
            using var shell = new ShellHarness();
            shell.Router.Navigate(Router.FallbackRoute);
            var oldView = shell.Router.CurrentView;

            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();
            shell.Router.Navigate("alpha");

            var newView = shell.Router.CurrentView;
            var surface = Assert.IsType<Grid>(shell.Host.Content);
            Assert.NotNull(oldView);
            Assert.NotNull(newView);
            Assert.True(shell.Router.TransitionActive);
            Assert.True(shell.FrameClock.Attached);
            Assert.Equal(2, surface.Children.Count);

            shell.FrameClock.Pulse(0);
            shell.FrameClock.Pulse(90);
            var oldLayer = surface.Children
                .OfType<ContentPresenter>()
                .Single(layer => ReferenceEquals(layer.Content, oldView));
            var newLayer = surface.Children
                .OfType<ContentPresenter>()
                .Single(layer => ReferenceEquals(layer.Content, newView));
            Assert.InRange(oldLayer.Opacity, 0, 0.999999);
            Assert.InRange(newLayer.Opacity, 0.000001, 1);

            shell.FrameClock.Pulse(180);

            Assert.False(shell.Router.TransitionActive);
            Assert.False(shell.FrameClock.Attached);
            Assert.Single(surface.Children);
            Assert.Same(newView, Assert.IsType<ContentPresenter>(surface.Children[0]).Content);
        });
    }

    [Fact]
    public void UnregisterDuringCrossfade_LeavesTheRetainedHost_AndKillsItOnEvict()
    {
        Sta.Run(() =>
        {
            PowerSaving.Set(false);
            using var shell = new ShellHarness();
            shell.Router.Navigate(Router.FallbackRoute);
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();
            shell.Router.Navigate("alpha");
            var hostLifetime = shell.Router.ViewLifetime!;

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();

            // Host is retained off-screen first, then destroyed by stale-cache
            // evict because the route left the registry — no crossfade from a
            // retained host (its layer is already removed).
            Assert.False(hostLifetime.Alive);
            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);
            Assert.IsType<FakeSettingsView>(shell.Router.CurrentView);
            Assert.False(shell.Router.TransitionActive);
            Assert.Single(Assert.IsType<Grid>(shell.Host.Content).Children);
        });
    }

    [Fact]
    public void ShellLifetime_DestroysTheDisplayedView_AndAnyTransition()
    {
        Sta.Run(() =>
        {
            PowerSaving.Set(false);
            var shell = new ShellHarness();
            shell.Router.Navigate(Router.FallbackRoute);
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();
            shell.Router.Navigate("alpha");
            var citizenLifetime = shell.Router.ViewLifetime!;
            Assert.True(shell.Router.TransitionActive);

            shell.Dispose();

            Assert.False(citizenLifetime.Alive);
            Assert.False(shell.Router.TransitionActive);
            Assert.False(shell.FrameClock.Attached);
            Assert.Null(shell.Router.CurrentView);
            Assert.Empty(Assert.IsType<Grid>(shell.Host.Content).Children);
        });
    }

}
