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
    public void NavigateAway_DestroysTheViewLifetime_AndReplacesTheReference()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();

            var destroyed = false;
            shell.Router.Navigate("alpha");
            var citizenLifetime = shell.Router.ViewLifetime!;
            citizenLifetime.Add(() => destroyed = true);

            shell.Router.Navigate(Router.FallbackRoute);

            Assert.True(destroyed);
            Assert.False(citizenLifetime.Alive);
            Assert.NotSame(citizenLifetime, shell.Router.ViewLifetime);
            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);
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
    public void Unregister_DisplayedRoute_LandsOnSettings_AndKillsTheLifetime()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();

            var destroyed = false;
            shell.Router.Navigate("alpha");
            var citizenLifetime = shell.Router.ViewLifetime!;
            citizenLifetime.Add(() => destroyed = true);

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();

            Assert.True(destroyed);
            Assert.False(citizenLifetime.Alive);
            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);

            // Settings owns a lifetime of its own now, so ViewLifetime is not
            // null — it is a different, live one.
            Assert.NotSame(citizenLifetime, shell.Router.ViewLifetime);
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

    /// <summary>
    /// The searcher isolates load and construction; CreateView runs here,
    /// later. Three shapes of failure, one requirement: the shell survives, the
    /// route is dropped, and no half-created view is attached.
    /// </summary>
    [Theory]
    [InlineData("throws")]
    [InlineData("null")]
    [InlineData("parented")]
    public void CreateViewFailure_IsIsolated_AndDropsTheCitizen(string mode)
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
            shell.Main.Pump();

            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);
            Assert.Empty(shell.Gate.Snapshot());
            Assert.Equal(RegistrationRefusal.ViewFailed, shell.Gate.Failures().Single().Reason);
            Assert.IsType<FakeSettingsView>(shell.Router.CurrentView); // never a half-created view
            if (mode == "parented") Assert.True(lifetimeDied);
        });
    }

    /// <summary>
    /// v0 needed `_currentRoute = ""` to defeat its own early-return when
    /// modules arrived late (MainWindow.xaml.cs:118). Re-navigating must work
    /// without that trick, or a late citizen can never replace a placeholder.
    /// </summary>
    [Fact]
    public void Navigate_ToTheSameRouteAgain_RebuildsWithoutAResetWorkaround()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            var descriptor = Fake.Descriptor("alpha");
            shell.Gate.Register(descriptor);
            shell.Main.Pump();

            shell.Router.Navigate("alpha");
            var first = shell.Router.ViewLifetime;
            shell.Router.Navigate("alpha");

            Assert.NotSame(first, shell.Router.ViewLifetime);
            Assert.False(first!.Alive);
            Assert.Equal(2, ((FakeModule)descriptor.Instance).CreateCount);
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
    /// RejectForFailedView unregisters the citizen and fires RegistryChanged
    /// synchronously, so the window forwards it to OnRegistryChanged while
    /// Navigate is still unwinding. Both paths land on the fallback, so without
    /// a reentrancy guard one failure builds Settings twice.
    /// </summary>
    [Fact]
    public void FailedRebuildOfTheDisplayedRoute_ShowsTheFallbackOnce()
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

            Assert.Equal([Router.FallbackRoute], navigations);
            Assert.Equal(1, shell.SettingsShown);
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
    public void UnregisterDuringCrossfade_CancelsTheOldTransition_AndKillsTheCitizen()
    {
        Sta.Run(() =>
        {
            PowerSaving.Set(false);
            using var shell = new ShellHarness();
            shell.Router.Navigate(Router.FallbackRoute);
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();
            shell.Router.Navigate("alpha");
            var citizenLifetime = shell.Router.ViewLifetime!;

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();

            Assert.False(citizenLifetime.Alive);
            Assert.Equal(Router.FallbackRoute, shell.Router.CurrentRoute);
            Assert.IsType<FakeSettingsView>(shell.Router.CurrentView);
            Assert.True(shell.Router.TransitionActive);
            Assert.Equal(2, Assert.IsType<Grid>(shell.Host.Content).Children.Count);

            shell.FrameClock.Pulse(0);
            shell.FrameClock.Pulse(180);
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
