using System.IO;
using System.Windows;
using System.Windows.Interop;
using Citadel.Core;
using Citadel.Core.Crl;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Searcher;
using Citadel.Setting;
using Citadel.Setting.Screens;
using Citadel.Ui.Animations;

namespace Citadel.Shell;

/// <summary>
/// The composition root — the single place allowed to know both sides. It
/// constructs the searcher and hands it the gate; no library references
/// module/.
///
/// Startup order is Log → MainQueue/Crl → Watchdog → Tokens → UI, and it is not
/// cosmetic. Crl.Post before Initialize is dropped *and recorded*
/// (Crl.cs:57-66), which only helps if Log is already running — v0 had no
/// logging at all, so there is no precedent to copy here.
///
/// MainQueue takes an injected wake delegate, which is why Citadel.Core never
/// sees a Dispatcher. This is where the two meet: v0's
/// Crl.Initialize(Dispatcher) does not port.
///
/// The searcher starts *after* the window is shown. Discovery is disk work, and
/// the complete Settings frame exists before Show — so screens arrive a beat
/// later through the same live path a folder
/// dropped at runtime uses. There is no separate startup path to get wrong.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// Where citizens are discovered: beside the executable, not the source
    /// tree. Watching `module/` in the repo would appear to work in development
    /// and never work once installed.
    /// </summary>
    internal const string ModuleFolderName = "module";

    private readonly Lifetime _appLifetime = new();
    private readonly SingleInstanceHost? _instanceHost;
    private Tokens? _tokens;
    private AnimationManager? _animations;
    private MainWindow? _window;
    private ResidentShell? _resident;
    private Watcher? _searcher;

    public App()
    {
    }

    internal App(SingleInstanceHost instanceHost) =>
        _instanceHost = instanceHost ?? throw new ArgumentNullException(nameof(instanceHost));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Start();
        Log.Main("[App] starting");

        var main = new MainQueue(
            wake: drain => Dispatcher.BeginInvoke(drain),
            isMain: () => Dispatcher.CheckAccess());
        Crl.Initialize(main);
        Crl.StartWatchdog();

        var tokens = new Tokens();
        foreach (var issue in LoadTokens(tokens, e.Args))
        {
            Log.Main($"[App] token issue on load: {issue.Token} {issue.Verdict} — {issue.Message}");
        }
        _tokens = tokens;

        var animations = new AnimationManager();
        _animations = animations;
        _appLifetime.Add(animations.Dispose);

        var gate = new ModuleGate(main, _appLifetime);
        var host = new ShellSettingHost(
            gate,
            tokens,
            () => _window,
            new VelopackUpdateService(),
            Shutdown);
        _appLifetime.Add(host.Detach);

        var window = new MainWindow(
            tokens,
            gate,
            animations,
            _appLifetime,
            BuiltInRoutes(host));
        _window = window;

        MainWindow = window;

        if (_instanceHost is not null)
        {
            var tray = TrayHost.TryCreate(out var trayError);
            if (tray is null)
            {
                Log.Main($"[Startup] tray unavailable; Close will exit: {trayError}");
            }

            var resident = new ResidentShell(
                window,
                host.CloseWindow,
                tray,
                _instanceHost.StopListening,
                Shutdown);
            _resident = resident;
            if (resident.ResidentEnabled)
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            }
            SessionEnding += OnSessionEnding;
        }

        window.Show();
        if (_instanceHost is not null && _resident is not null)
        {
            var windowHandle = new WindowInteropHelper(window).Handle;
            _instanceHost.MarkReady(windowHandle, _resident.RequestOpenAsync);
        }
        Log.Main("[App] window shown");

        _searcher = StartSearcher(main, gate, host);
    }

    /// <summary>
    /// Constructs the searcher, gives it the runtime module root and the gate,
    /// and bridges its two outputs back: log lines to the Modules sink, failure
    /// changes to Settings on the main thread.
    ///
    /// This is the whole of the permitted exception. The searcher receives no
    /// Shell type, and Shell learns nothing about manifests or load contexts.
    /// </summary>
    private Watcher StartSearcher(MainQueue main, ModuleGate gate, ShellSettingHost host)
    {
        var root = Path.Combine(AppContext.BaseDirectory, ModuleFolderName);
        var searcher = new Watcher(
            root,
            gate,
            ModuleGate.ReservedRoutes,
            Log.Modules);

        host.AttachSearcher(searcher.RequestRediscovery, searcher.Failures);

        // The searcher raises on its own pump thread. Guarded-posting through
        // MainQueue is what keeps Settings' Changed event a main-thread event,
        // and drops it outright once the app lifetime is over.
        searcher.FailuresChanged += () => main.Post(_appLifetime, host.NotifyChanged);

        // Application lifetime, not window visibility: Close hides the window and
        // must leave discovery running, so only real exit disposes this.
        _appLifetime.Add(searcher.Dispose);

        searcher.Start();
        Log.Main($"[App] searcher watching '{root}'");
        return searcher;
    }

    /// <summary>
    /// Settings is the one built-in route in the main shell. Its three reserved
    /// sub-screens are hosted by ShellSettingHost in a separate settings window;
    /// none of the four names passes through the module gate.
    ///
    /// Internal and static so a test can compose the same map the app does,
    /// instead of a parallel one that can drift.
    /// </summary>
    internal static Dictionary<string, BuiltInRoute> BuiltInRoutes(ISettingHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return new Dictionary<string, BuiltInRoute>(StringComparer.Ordinal)
        {
            [Router.FallbackRoute] = new(
                "Settings",
                lifetime => new SettingsScreen(host, lifetime),
                SettingsLayout.Declaration()),
        };
    }

    /// <summary>
    /// Applies the executable-only reset floor before normal token loading.
    /// The executable handles `--reset-ui` before normal token loading.
    /// </summary>
    internal static IReadOnlyList<GuardIssue> LoadTokens(Tokens tokens, IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Any(arg => string.Equals(arg, "--reset-ui", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Main("[App] --reset-ui requested");
            tokens.ResetAll();
        }
        return tokens.Load();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Main("[App] exiting");
        SessionEnding -= OnSessionEnding;
        _resident?.Dispose();
        _resident = null;
        // _appLifetime.Destroy() disposes the searcher: watchers detached, pump
        // cancelled, retained load contexts unloaded. Once, at real exit.
        _appLifetime.Destroy();
        _searcher = null;
        _tokens?.Save();
        Log.Finish();
        base.OnExit(e);
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs args) =>
        _resident?.PrepareForSessionEnd();
}
