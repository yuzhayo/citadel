using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Crl;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Ui.Animations;
using Citadel.Ui.Controls;

namespace Citadel.Ui.Tests;

[Collection("PowerSaving serial")]
public sealed class AnimationManagerTests
{
    [Fact]
    public void OneClockServesAllAnimationsAndDetachesDirectlyAtZero()
    {
        var clock = new FakeFrameClock();
        using var manager = new AnimationManager(clock);
        var lifetime = new Lifetime();
        var first = new List<double>();
        var second = new List<double>();

        manager.Start(lifetime, TimeSpan.FromMilliseconds(100), first.Add);
        manager.Start(lifetime, TimeSpan.FromMilliseconds(200), second.Add);

        Assert.True(manager.ClockAttached);
        Assert.Equal(1, clock.AttachCount);
        Assert.Equal(2, manager.ActiveCount);

        clock.Pulse(0);
        clock.Pulse(100);
        Assert.True(manager.ClockAttached);
        Assert.Equal(1d, first[^1]);
        Assert.Equal(0.5d, second[^1]);

        clock.Pulse(200);
        Assert.False(manager.ClockAttached);
        Assert.Equal(0, manager.ActiveCount);
        Assert.Equal(1, clock.DetachCount);
        Assert.Equal(1d, second[^1]);
    }

    [Fact]
    public void StartDuringTickJoinsAtTheNextTickBoundary()
    {
        var clock = new FakeFrameClock();
        using var manager = new AnimationManager(clock);
        var lifetime = new Lifetime();
        var childTicks = 0;
        Animation? child = null;

        manager.Start(lifetime, TimeSpan.FromMilliseconds(100), progress =>
        {
            if (progress == 0 && child is null)
            {
                child = manager.Start(
                    lifetime,
                    TimeSpan.FromMilliseconds(100),
                    _ => childTicks++);
            }
        });

        clock.Pulse(0);
        Assert.NotNull(child);
        Assert.Equal(0, childTicks);
        Assert.Equal(2, manager.ActiveCount);

        clock.Pulse(10);
        Assert.Equal(1, childTicks);
    }

    [Fact]
    public void StopAndLifetimeDestroyInsideTickDoNotMutateTheVisitedSet()
    {
        var clock = new FakeFrameClock();
        using var manager = new AnimationManager(clock);
        var selfStopLifetime = new Lifetime();
        var destroyLifetime = new Lifetime();
        Animation? selfStopping = null;
        var destroyedTicks = 0;

        selfStopping = manager.Start(
            selfStopLifetime,
            TimeSpan.FromSeconds(1),
            _ => selfStopping!.Stop());
        manager.Start(
            destroyLifetime,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                destroyedTicks++;
                destroyLifetime.Destroy();
            });

        clock.Pulse(0);

        Assert.False(selfStopping.IsRunning);
        Assert.Equal(1, destroyedTicks);
        Assert.Equal(0, manager.ActiveCount);
        Assert.False(manager.ClockAttached);
    }

    [Fact]
    public void SameAnimationCannotBeRegisteredTwice()
    {
        var clock = new FakeFrameClock();
        using var manager = new AnimationManager(clock);
        var lifetime = new Lifetime();
        var ticks = 0;
        var animation = manager.Create(TimeSpan.FromMilliseconds(10), _ => ticks++);

        Assert.True(animation.Start(lifetime));
        Assert.False(animation.Start(lifetime));
        Assert.Equal(1, manager.ActiveCount);

        clock.Pulse(0);
        clock.Pulse(10);
        Assert.Equal(2, ticks);
        Assert.False(manager.ClockAttached);
    }

    [Fact]
    public void PowerSavingSnapsOnTheNextTickAndThenDetaches()
    {
        PowerSaving.Set(false);
        try
        {
            var clock = new FakeFrameClock();
            using var manager = new AnimationManager(clock);
            var lifetime = new Lifetime();
            var values = new List<double>();
            manager.Start(lifetime, TimeSpan.FromMinutes(1), values.Add);

            clock.Pulse(0);
            Assert.Equal(0d, values[^1]);

            PowerSaving.Set(true);
            Assert.True(manager.ClockAttached);
            Assert.Equal(0d, values[^1]);

            clock.Pulse(1);
            Assert.Equal(1d, values[^1]);
            Assert.False(manager.ClockAttached);
            Assert.Equal(0, manager.ActiveCount);
        }
        finally
        {
            PowerSaving.Set(false);
        }
    }

    [Fact]
    public void SidebarCollapseInterpolatesOnlyWidthAndOpacityThenPowerSavingSnapsBoth()
    {
        PowerSaving.Set(false);
        try
        {
            StaTest.Run(() =>
            {
                var tokens = new Tokens();
                var clock = new FakeFrameClock();
                using var manager = new AnimationManager(clock);
                var lifetime = new Lifetime();
                var sidebar = new Sidebar();
                sidebar.Entries.Add(new NavEntry("home", "Home", "\uE80F"));
                sidebar.Attach(tokens, manager, lifetime);
                WpfLayout.Arrange(sidebar, sidebar.CurrentWidth);

                var navList = Assert.IsType<ListBox>(
                    sidebar.Template.FindName(Sidebar.NavListPart, sidebar));
                var row = Assert.IsType<ListBoxItem>(
                    navList.ItemContainerGenerator.ContainerFromIndex(0));
                var titleHost = Assert.Single(
                    WpfLayout.Descendants<Border>(row),
                    element => Equals(element.Tag, "NavTitleHost"));
                sidebar.SetCollapsed(true, animate: true);

                clock.Pulse(0);
                Assert.Equal(sidebar.FullWidth, sidebar.CurrentWidth, precision: 6);
                Assert.Equal(1d, sidebar.TitleOpacity, precision: 6);

                clock.Pulse(90);
                Assert.InRange(sidebar.CurrentWidth, tokens.Number("Rail"), sidebar.FullWidth);
                Assert.InRange(sidebar.TitleOpacity, 0d, 1d);
                WpfLayout.Arrange(sidebar, sidebar.CurrentWidth);
                Assert.Equal(Visibility.Visible, titleHost.Visibility);
                Assert.True(titleHost.ClipToBounds);
                Assert.Equal(sidebar.TitleOpacity, titleHost.Opacity, precision: 6);

                PowerSaving.Set(true);
                clock.Pulse(91);
                Assert.Equal(tokens.Number("Rail"), sidebar.CurrentWidth, precision: 6);
                Assert.Equal(0d, sidebar.TitleOpacity, precision: 6);
                Assert.False(manager.ClockAttached);

                lifetime.Destroy();
            });
        }
        finally
        {
            PowerSaving.Set(false);
        }
    }
}

[CollectionDefinition("PowerSaving serial", DisableParallelization = true)]
public sealed class PowerSavingSerialCollection;
