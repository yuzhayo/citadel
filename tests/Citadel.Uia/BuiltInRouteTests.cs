using System.Windows.Automation;
using System.Windows;
using System.Text.Json.Nodes;
using Citadel.Core;
using Citadel.Core.Rpl;
using Citadel.Setting.Screens;
using Citadel.Shell;
using Citadel.Ui.Animations;

namespace Citadel.Uia;

/// <summary>
/// Settings stays in the Content card, its
/// three editors live in one separate Shell-owned window, and all four names
/// remain reserved against citizens.
/// </summary>
public class BuiltInRouteTests
{
    [Fact]
    public void Settings_IsTheOnlyBuiltInRouteInTheContentCard()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();

            Assert.Single(App.BuiltInRoutes(fixture.SettingHost));
            Assert.Equal(Router.FallbackRoute, window.Router.CurrentRoute);
            Assert.Equal(
                "SettingsScreen",
                AutomationProperties.GetAutomationId(window.Router.CurrentView));
            Assert.Equal("Settings", window.ContentHeaderElement.Text);
        });
    }

    [Fact]
    public void SettingsButtons_OpenOneReusablePopup_OutsideThePreviewTarget()
    {
        Sta.Run(() =>
        {
            var lifetime = new Lifetime();
            var animations = new AnimationManager();
            var main = new TestMain();
            var tokens = Fake.Store();
            var gate = new ModuleGate(main.Queue, lifetime);
            MainWindow? owner = null;
            var host = new ShellSettingHost(gate, tokens, () => owner);
            owner = new MainWindow(
                tokens,
                gate,
                animations,
                lifetime,
                App.BuiltInRoutes(host));
            owner.ShowInTaskbar = false;
            owner.Show();

            try
            {
                var settings = Assert.IsType<SettingsScreen>(owner.Router.CurrentView);
                Assert.True(tokens.CommitCore("FullMax", JsonValue.Create(232)).Applied);

                settings.Click("OpenAppearance");
                var popup = host.OpenWindow;
                Assert.NotNull(popup);
                Assert.Same(owner, popup!.Owner);
                Assert.True(popup.IsVisible);
                Assert.Equal(SettingsScreen.AppearanceRoute, popup.CurrentRoute);
                Assert.Equal("Appearance", popup.RouteTitle);
                var appearance = Assert.IsType<AppearanceScreen>(popup.CurrentView);
                var appearanceLifetime = popup.ViewLifetime!;
                var popupWidth = popup.ActualWidth;

                // Exact crash reproduction: the editor stays fixed outside the
                // target, and guarded preview never exposes 50 > 39 to Sidebar.
                appearance.BeginDrag("FullMax");
                appearance.DragTo(39);
                Assert.Equal(232, tokens.Number("FullMax"));
                Assert.Equal(popupWidth, popup.ActualWidth);
                Assert.Equal(Router.FallbackRoute, owner.Router.CurrentRoute);
                appearance.CancelDrag();

                settings.Click("OpenLayout");
                Assert.Same(popup, host.OpenWindow);
                Assert.Equal(SettingsScreen.LayoutRoute, popup.CurrentRoute);
                Assert.IsType<ModuleLayoutScreen>(popup.CurrentView);
                Assert.False(appearanceLifetime.Alive);

                settings.Click("OpenAppearance");
                appearance = Assert.IsType<AppearanceScreen>(popup.CurrentView);
                var closingLifetime = popup.ViewLifetime!;
                appearance.BeginDrag("Row");
                appearance.DragTo(48);
                Assert.True(tokens.HasPreview);

                host.CloseWindow();

                Assert.False(closingLifetime.Alive);
                Assert.False(tokens.HasPreview);
                Assert.Null(host.OpenWindow);
            }
            finally
            {
                host.Detach();
                if (owner.IsVisible) owner.Close();
                lifetime.Destroy();
                animations.Dispose();
            }
        });
    }

    /// <summary>Only `settings` is a sidebar entry; the other three are not.</summary>
    [Fact]
    public void OnlySettings_AppearsInTheSidebar()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();

            var routes = window.SidebarControl.MainEntries
                .Concat(window.SidebarControl.PinnedEntries)
                .Select(entry => entry.Route)
                .ToList();

            Assert.Equal([Router.FallbackRoute], routes);
            Assert.DoesNotContain(SettingsScreen.AppearanceRoute, routes);
            Assert.DoesNotContain(SettingsScreen.LayoutRoute, routes);
            Assert.DoesNotContain(SettingsScreen.GalleryRoute, routes);
        });
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("settings/appearance")]
    [InlineData("settings/layout")]
    [InlineData("settings/gallery")]
    public void AllFourSettingsRoutes_AreReservedAgainstCitizens(string route)
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Gate.Register(Fake.Descriptor(route, "Impostor"));
            shell.Main.Pump();

            Assert.Empty(shell.Gate.Snapshot());
            Assert.Equal(
                RegistrationRefusal.ReservedRoute,
                shell.Gate.Failures().Single().Reason);
            Assert.Contains($"'{route}' is a reserved core route", Log.Full());
        });
    }

    /// <summary>With nothing installed the shell still shows a complete Settings.</summary>
    [Fact]
    public void EmptyRegistry_StillShowsACompleteSettingsFrame()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();

            Assert.Empty(fixture.Gate.Snapshot());
            Assert.Equal(Router.FallbackRoute, window.Router.CurrentRoute);
            Assert.Equal("Settings", window.ContentHeaderElement.Text);
            Assert.Equal("CITADEL", window.AppBrandElement.Text);
            Assert.Equal(
                "SettingsScreen",
                AutomationProperties.GetAutomationId(window.Router.CurrentView));
        });
    }

    [Fact]
    public void SettingsOwnLayout_IsAppliedByTheBuiltInRouter()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            var settings = Assert.IsType<SettingsScreen>(window.Router.CurrentView);
            var problems = Assert.IsAssignableFrom<FrameworkElement>(settings.FindName("problems"));

            Assert.True(fixture.Tokens.CommitLayout(
                Router.FallbackRoute,
                "problems",
                "visible",
                JsonValue.Create(false)));

            Assert.Equal(Visibility.Collapsed, problems.Visibility);
        });
    }
}
