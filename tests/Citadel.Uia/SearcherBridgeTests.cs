using System.IO;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Searcher;
using Citadel.Setting.Screens;
using Citadel.Shell;
using Citadel.Ui.Animations;

namespace Citadel.Uia;

/// <summary>
/// Settings shows both failure sources, rediscovery
/// reaches the searcher, and the searcher's lifetime belongs to the application
/// rather than to window visibility.
///
/// These compose the real <c>ShellSettingHost</c> rather than the stub, because
/// the merge of two failure sources is the thing under test.
/// </summary>
[Collection("Shell power saving serial")]
public class SearcherBridgeTests
{
    /// <summary>
    /// A duplicate route and a broken folder are different problems with
    /// different fixes, so neither may hide the other.
    /// </summary>
    [Fact]
    public void SettingsListsGateRefusalsAndSearcherFailuresTogether()
    {
        Sta.Run(() =>
        {
            using var shell = new SearcherShell();
            var settings = Assert.IsType<SettingsScreen>(shell.Window.Router.CurrentView);

            // Gate side: a reserved route is refused by the gate, not the folder.
            shell.Gate.Register(Fake.Descriptor("settings", "Impostor"));
            shell.Main.Pump();

            // Searcher side: a folder whose manifest cannot be read.
            shell.Modules.Folder("broken", ("module.json", "{ }"));
            shell.Searcher.Start();
            Wait.For(
                () => shell.Searcher.Failures().Any(failure => failure.Folder == "broken"),
                "the searcher should record the broken folder");
            shell.Host.NotifyChanged();

            var visible = settings.VisibleFailures;
            Assert.Contains(visible, line => line.Contains("reserved route"));
            Assert.Contains(visible, line => line.Contains("broken") && line.Contains("module.json"));
        });
    }

    /// <summary>
    /// `Update modules` re-raises the same scan through the installed searcher.
    /// </summary>
    [Fact]
    public void UpdateModulesRegistersAFolderTheWatcherWasNotWatchingFor()
    {
        Sta.Run(() =>
        {
            using var shell = new SearcherShell();
            var settings = Assert.IsType<SettingsScreen>(shell.Window.Router.CurrentView);

            // Present before Start, so the manual rescan is what finds it.
            shell.Modules.Citizen("blank");
            settings.Click("UpdateModules");
            shell.Searcher.Start();

            Wait.For(
                () => HasRoute(shell, "blank"),
                "the manual rescan must reach the searcher and register the folder");
        });
    }

    /// <summary>
    /// Once Close only hides the window, a folder can be dropped while nothing is
    /// visible. Discovery must keep running and the sidebar must be correct when
    /// the window comes back.
    /// </summary>
    [Fact]
    public void AFolderDroppedWhileTheWindowIsHiddenIsInTheSidebarOnReopen()
    {
        Sta.Run(() =>
        {
            using var shell = new SearcherShell(showWindow: true);
            var tray = new FakeTrayHost();
            var resident = new ResidentShell(
                shell.Window, shell.Host.CloseWindow, tray, () => { }, () => { });

            try
            {
                shell.Searcher.Start();
                Wait.For(() => Directory.Exists(shell.Modules.Root), "the root should exist");

                shell.Window.Close();
                Assert.False(shell.Window.IsVisible);

                shell.Modules.Fixture("dropped", "Shared");
                Wait.For(
                    () => HasRoute(shell, "shared"),
                    "a folder dropped while hidden should still register");

                // Through the tray, the way a user reopens it.
                tray.RequestOpen();

                Assert.True(shell.Window.IsVisible);
                Assert.Contains(
                    "shared",
                    shell.Window.SidebarControl.MainEntries
                        .Concat(shell.Window.SidebarControl.PinnedEntries)
                        .Select(entry => entry.Route));
            }
            finally
            {
                resident.Dispose();
            }
        });
    }

    private static bool HasRoute(SearcherShell shell, string route)
    {
        shell.Main.Pump();
        return shell.Gate.Snapshot().Any(descriptor => descriptor.Route == route);
    }

    /// <summary>A tray whose Open the test raises, so Close hides rather than exits.</summary>
    private sealed class FakeTrayHost : ITrayHost
    {
        public event Action? OpenRequested;

        public event Action? ExitRequested;

        internal void RequestOpen() => OpenRequested?.Invoke();

        internal void RequestExit() => ExitRequested?.Invoke();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A real window, a real gate, the real ShellSettingHost, and a searcher over a
/// temp module root — composed the way App composes them.
/// </summary>
internal sealed class SearcherShell : IDisposable
{
    private readonly Lifetime _lifetime = new();
    private readonly AnimationManager _animations = new();

    public SearcherShell(bool showWindow = false)
    {
        Main = new TestMain();
        Tokens = Fake.Store();
        Gate = new ModuleGate(Main.Queue, _lifetime);
        Modules = ModuleFolder.Create();

        MainWindow? window = null;
        Host = new ShellSettingHost(Gate, Tokens, () => window);
        window = new MainWindow(Tokens, Gate, _animations, _lifetime, App.BuiltInRoutes(Host));
        Window = window;
        Window.ShowInTaskbar = false;
        if (showWindow) Window.Show();

        Searcher = new Watcher(Modules.Root, Gate, ModuleGate.ReservedRoutes, _ => { });
        Host.AttachSearcher(Searcher.RequestRediscovery, Searcher.Failures);
        Searcher.FailuresChanged += () => Main.Queue.Post(_lifetime, Host.NotifyChanged);
    }

    public TestMain Main { get; }

    public Tokens Tokens { get; }

    public ModuleGate Gate { get; }

    public ShellSettingHost Host { get; }

    public MainWindow Window { get; }

    public Watcher Searcher { get; }

    public ModuleFolder Modules { get; }

    public void Dispose()
    {
        Searcher.Dispose();
        Host.Detach();
        if (Window.IsVisible) Window.Close();
        _lifetime.Destroy();
        _animations.Dispose();
        Modules.Dispose();
    }
}
