using System.Text.Json.Nodes;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Crl;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Shell;
using Citadel.Ui.Animations;
using Citadel.Ui.Controls;

namespace Citadel.Uia;

/// <summary>
/// The window itself: collapse widths across a token edit, the always-
/// present Settings entry, per-route AutomationIds, and power saving driven by
/// window state.
///
/// These construct the real MainWindow on an STA thread without showing it.
/// Real-window evidence — actual bounds, duplicate/missing AutomationIds — comes
/// from the wpf-buddy MCP server against the running exe, because a test that
/// re-derives the arithmetic proves the arithmetic and not the layout.
/// </summary>
[Collection("Shell power saving serial")]
public class MainWindowTests : IDisposable
{
    public void Dispose() => PowerSavingReset();

    private static void PowerSavingReset() => PowerSaving.Set(false);

    private static void WithWindow(Action<MainWindow, ShellFixture> body)
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            body(window, fixture);
        });
    }

    /// <summary>
    /// Some behaviour only exists once the window has an HWND: StateChanged does
    /// not fire for a window that was never shown, and a ListBox generates no
    /// item containers until it is really laid out. Those tests need this.
    /// </summary>
    private static void WithShownWindow(Action<MainWindow, ShellFixture> body)
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            window.ShowInTaskbar = false;
            window.Show();
            try
            {
                window.UpdateLayout();
                body(window, fixture);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Window_SizeAndMinimum_ComeFromTokens_NotLiterals()
    {
        WithWindow((window, fixture) =>
        {
            Assert.Equal(fixture.Tokens.Number("WindowW"), window.Width);
            Assert.Equal(fixture.Tokens.Number("WindowH"), window.Height);
            Assert.Equal(fixture.Tokens.Number("WindowMinW"), window.MinWidth);
            Assert.Equal(fixture.Tokens.Number("WindowMinH"), window.MinHeight);

            Assert.True(fixture.Tokens.CommitCore("WindowMinW", JsonValue.Create(1000)).Applied);
            Assert.Equal(1000, window.MinWidth);
        });
    }

    [Fact]
    public void LoadedWindow_KeepsTheUsersSize_WhenAnUnrelatedTokenChanges()
    {
        WithShownWindow((window, fixture) =>
        {
            // Stay inside the runner's work area. A hosted Windows runner may
            // be only 1024x768, where WPF coerces a literal 1400x800 before the
            // token change under test even happens.
            var userWidth = Math.Clamp(
                SystemParameters.WorkArea.Width - 100,
                window.MinWidth,
                1000);
            var userHeight = Math.Clamp(
                SystemParameters.WorkArea.Height - 100,
                window.MinHeight,
                700);
            window.Width = userWidth;
            window.Height = userHeight;

            Assert.True(fixture.Tokens.CommitCore(
                "BgRail", JsonValue.Create("#202020")).Applied);

            Assert.Equal(userWidth, window.Width);
            Assert.Equal(userHeight, window.Height);
        });
    }

    [Fact]
    public void ResetUiArgument_DeletesOverridesBeforeNormalLoad()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "citadel-reset-ui-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "ui.json");
        try
        {
            var seeded = new Tokens(path);
            Assert.True(seeded.CommitCore("Rail", JsonValue.Create(72)).Applied);
            seeded.Save();
            Assert.True(File.Exists(path));

            var reset = new Tokens(path);
            App.LoadTokens(reset, ["--reset-ui"]);

            Assert.Equal(Defaults.All["Rail"].Number, reset.Number("Rail"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingsEntry_IsAlwaysPresent_AndIsTheStartingRoute()
    {
        WithWindow((window, _) =>
        {
            Assert.Contains(
                window.SidebarControl.PinnedEntries,
                entry => entry.Route == NavEntry.Settings.Route);
            Assert.Equal(Router.FallbackRoute, window.Router.CurrentRoute);
            Assert.Equal("Settings", window.ContentHeaderElement.Text);
        });
    }

    [Fact]
    public void ShellHierarchy_HasOneHeaderSidebarAndContentFrame()
    {
        WithShownWindow((window, _) =>
        {
            Assert.Equal("AppHeader", AutomationProperties.GetAutomationId(window.AppHeaderElement));
            Assert.Equal("AppBrand", AutomationProperties.GetAutomationId(window.AppBrandElement));
            Assert.Equal("CollapseToggle", AutomationProperties.GetAutomationId(window.CollapseToggleControl));
            Assert.Equal("SidebarCard", AutomationProperties.GetAutomationId(window.SidebarControl));
            Assert.Equal("ContentCard", AutomationProperties.GetAutomationId(window.ContentCardElement));
            Assert.Equal("ContentHeader", AutomationProperties.GetAutomationId(window.ContentHeaderElement));
            Assert.Equal("CITADEL", window.AppBrandElement.Text);
            Assert.Equal(
                1,
                Descendants<TextBlock>(window).Count(text => text.Text == "CITADEL"));
            // The hierarchy assertion is about the frame, not which body fills it.
            Assert.Equal(
                "SettingsScreen",
                AutomationProperties.GetAutomationId(window.Router.CurrentView));
        });
    }

    [Fact]
    public void AppHeaderToggle_OwnsTheExistingSidebarCollapseState()
    {
        WithWindow((window, _) =>
        {
            window.SidebarControl.SetCollapsed(false, animate: false);

            window.CollapseToggleControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.SidebarControl.IsCollapsed);

            window.CollapseToggleControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.SidebarControl.IsCollapsed);
        });
    }

    [Fact]
    public void AcceptedCitizenTitle_DrivesHeader_AndUnregisterFallsBackTogether()
    {
        WithWindow((window, fixture) =>
        {
            fixture.Gate.Register(Fake.Descriptor("alpha", "Alpha workspace"));
            fixture.Main.Pump();

            window.Router.Navigate("alpha");
            Assert.Equal("alpha", window.Router.CurrentRoute);
            Assert.Equal("Alpha workspace", window.ContentHeaderElement.Text);

            fixture.Gate.Unregister("alpha");
            fixture.Main.Pump();
            Assert.Equal(Router.FallbackRoute, window.Router.CurrentRoute);
            Assert.Equal("Settings", window.ContentHeaderElement.Text);
        });
    }

    [Fact]
    public void OptionalCitizenHeaderAction_AppearsAndClearsWithItsRoute()
    {
        WithWindow((window, fixture) =>
        {
            var action = new Button { Content = "Refresh" };
            fixture.Gate.Register(Fake.Descriptor(
                "alpha",
                create: _ => new HeaderActionView(action)));
            fixture.Main.Pump();

            window.Router.Navigate("alpha");
            Assert.Same(action, window.ContentHeaderActionElement.Content);

            window.Router.Navigate(Router.FallbackRoute);
            Assert.Null(window.ContentHeaderActionElement.Content);
        });
    }

    [Fact]
    public void FailedCitizenView_DoesNotLeaveItsTitleInTheHeader()
    {
        WithWindow((window, fixture) =>
        {
            fixture.Gate.Register(Fake.Descriptor(
                "broken",
                "Broken title",
                create: _ => throw new InvalidOperationException("boom")));
            fixture.Main.Pump();

            window.Router.Navigate("broken");

            Assert.Equal(Router.FallbackRoute, window.Router.CurrentRoute);
            Assert.Equal("Settings", window.ContentHeaderElement.Text);
        });
    }

    /// <summary>
    /// Collapsed is exactly the Rail token; expanded is the measured formula
    /// clamped to [FullMin, FullMax]. Then the tokens move and both must follow —
    /// checking only the defaults would miss invalid live edits.
    /// </summary>
    [Fact]
    public void CollapsedWidthIsRail_AndExpandedFollowsTheFormula_AcrossATokenEdit()
    {
        WithWindow((window, fixture) =>
        {
            var sidebar = window.SidebarControl;
            var tokens = fixture.Tokens;

            double Expected() => Sidebar.CalculateFullWidth(
                sidebar.MainEntries.Concat(sidebar.PinnedEntries),
                tokens,
                sidebar.TitleFontFamily,
                sidebar.TitleFontSize);

            sidebar.SetCollapsed(false, animate: false);
            Assert.Equal(Expected(), sidebar.CurrentWidth, precision: 6);
            Assert.InRange(sidebar.CurrentWidth, tokens.Number("FullMin"), tokens.Number("FullMax"));

            sidebar.SetCollapsed(true, animate: false);
            Assert.Equal(tokens.Number("Rail"), sidebar.CurrentWidth);

            Assert.True(tokens.CommitCore("Rail", JsonValue.Create(72)).Applied);
            Assert.Equal(72, sidebar.CurrentWidth);

            sidebar.SetCollapsed(false, animate: false);
            Assert.True(tokens.CommitCore("FullMax", JsonValue.Create(210)).Applied);
            Assert.Equal(Expected(), sidebar.CurrentWidth, precision: 6);
            Assert.InRange(sidebar.CurrentWidth, tokens.Number("FullMin"), tokens.Number("FullMax"));
        });
    }

    /// <summary>
    /// v0's NavTitle/PinTitle sat inside DataTemplates, so every row derived the
    /// same AutomationId and no row could be addressed. The
    /// item container binds the route instead; this asserts the ids are distinct
    /// per row. The real visual tree is checked by wpf_detect_duplicate_ids.
    /// </summary>
    [Fact]
    public void EntryAutomationIds_AreThePerRouteValues_NotOneSharedName()
    {
        WithShownWindow((window, fixture) =>
        {
            fixture.Gate.Register(Fake.Descriptor("alpha", "Alpha"));
            fixture.Gate.Register(Fake.Descriptor("beta", "Beta"));
            fixture.Main.Pump();
            window.UpdateLayout();

            var ids = Descendants<ListBoxItem>(window)
                .Select(AutomationProperties.GetAutomationId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            Assert.Contains("alpha", ids);
            Assert.Contains("beta", ids);
            Assert.Contains(NavEntry.Settings.Route, ids);
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Fact]
    public void RegisteredCitizen_AppearsInTheSidebar_WithNoFilesystem()
    {
        WithWindow((window, fixture) =>
        {
            fixture.Gate.Register(Fake.Descriptor("alpha", "Alpha"));
            fixture.Main.Pump();

            Assert.Contains(window.SidebarControl.MainEntries, entry => entry.Route == "alpha");
        });
    }

    /// <summary>A null icon must not drop the entry or throw.</summary>
    [Fact]
    public void NullIcon_StillProducesAnEntry()
    {
        WithWindow((window, fixture) =>
        {
            fixture.Gate.Register(Fake.Descriptor("alpha", "Alpha", icon: null));
            fixture.Main.Pump();

            var entry = window.SidebarControl.MainEntries.Single();
            Assert.Equal(string.Empty, entry.Icon);
        });
    }

    /// <summary>
    /// PowerSaving.Enabled == true means saving is ON. Citizen heartbeats read
    /// this flag, so the window must drive it.
    /// </summary>
    [Fact]
    public void WindowState_DrivesPowerSaving()
    {
        WithShownWindow((window, _) =>
        {
            Assert.False(PowerSaving.Enabled); // shown and visible: saving OFF

            window.WindowState = WindowState.Minimized;
            Assert.True(PowerSaving.Enabled);

            window.WindowState = WindowState.Normal;
            Assert.False(PowerSaving.Enabled);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

internal sealed class HeaderActionView(FrameworkElement action)
    : Border, IContentHeaderActionProvider
{
    public FrameworkElement CreateContentHeaderAction() => action;
}

/// <summary>Everything a MainWindow needs, with a manual main queue.</summary>
internal sealed class ShellFixture : IDisposable
{
    private readonly Lifetime _lifetime = new();
    private readonly AnimationManager _animations = new();

    public ShellFixture()
    {
        Main = new TestMain();
        Tokens = Fake.Store();
        Gate = new ModuleGate(Main.Queue, _lifetime);
        SettingHost = new StubSettingHost();
    }

    public TestMain Main { get; }

    public Tokens Tokens { get; }

    public ModuleGate Gate { get; }

    public StubSettingHost SettingHost { get; }

    /// <summary>
    /// The same built-in map App composes, so a test cannot pass against a
    /// parallel map that has drifted from the real composition root.
    /// </summary>
    public MainWindow CreateWindow() => new(
        Tokens,
        Gate,
        _animations,
        _lifetime,
        App.BuiltInRoutes(SettingHost));

    public void Dispose()
    {
        _lifetime.Destroy();
        _animations.Dispose();
    }
}
