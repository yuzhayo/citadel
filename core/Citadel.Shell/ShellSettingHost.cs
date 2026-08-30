using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Tokens;
using Citadel.Searcher;
using Citadel.Setting;
using Citadel.Setting.Screens;

namespace Citadel.Shell;

/// <summary>
/// Fills in the seam Settings declares, using the gate and a Shell-owned
/// settings window.
///
/// The direction matters: `Citadel.Setting` owns the interface and references
/// only Core and Contract, while Shell — which knows both — supplies the data.
/// Settings therefore never references Shell or the searcher; rediscovery stays
/// behind this seam.
///
/// This is also the merge point for two failure sources that must both
/// stay visible: the gate refuses duplicate and reserved routes and knows nothing
/// about folders, while the searcher fails folders and knows nothing about the
/// registry. Neither is hidden behind the other.
/// </summary>
internal sealed class ShellSettingHost : ISettingHost
{
    private readonly ModuleGate _gate;
    private readonly Tokens _tokens;
    private readonly Func<Window?> _owner;
    private readonly AppUpdateController _updateController;
    private bool _detached;
    private SettingsWindow? _settingsWindow;
    private Action? _rediscover;
    private Func<IReadOnlyList<ModuleFailure>>? _searcherFailures;

    public ShellSettingHost(ModuleGate gate, Tokens tokens, Func<Window?> owner)
        : this(
            gate,
            tokens,
            owner,
            new VelopackUpdateService(),
            static () => System.Windows.Application.Current?.Shutdown())
    {
    }

    internal ShellSettingHost(
        ModuleGate gate,
        Tokens tokens,
        Func<Window?> owner,
        IAppUpdateService updates,
        Action requestExit)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _updateController = new AppUpdateController(
            updates ?? throw new ArgumentNullException(nameof(updates)),
            requestExit ?? throw new ArgumentNullException(nameof(requestExit)),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);

        _gate.RegistryChanged += OnGateChanged;
        _gate.RegistrationRefused += OnRefused;
        _updateController.Changed += OnUpdateChanged;
    }

    public event Action? Changed;

    /// <summary>
    /// Shell hands the searcher's rescan and failure snapshot in here. An
    /// internal setter rather than a wider <see cref="ISettingHost"/>: Settings
    /// asks for rediscovery and reads failures through the seam it already has,
    /// so the searcher stays invisible to it.
    /// </summary>
    internal void AttachSearcher(
        Action rediscover,
        Func<IReadOnlyList<ModuleFailure>> failures)
    {
        _rediscover = rediscover ?? throw new ArgumentNullException(nameof(rediscover));
        _searcherFailures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    /// <summary>
    /// Called by App on the main thread after the searcher's failures changed.
    /// The searcher raises on its own pump thread; App owns the marshalling
    /// because the searcher has no queue by design.
    /// </summary>
    internal void NotifyChanged() => Changed?.Invoke();

    public IReadOnlyList<ModuleDescriptor> Screens() => _gate.Snapshot();

    /// <summary>
    /// Gate refusals and searcher folder failures, in that order. Both sources
    /// survive: a duplicate route and a broken manifest are different problems
    /// with different fixes.
    /// </summary>
    public IReadOnlyList<ScreenFailure> Failures()
    {
        var failures = _gate.Failures()
            .Select(failure => new ScreenFailure(
                failure.Route,
                $"{Describe(failure.Reason)}: {failure.Message}"))
            .ToList();

        if (_searcherFailures is not null)
        {
            failures.AddRange(_searcherFailures().Select(failure => new ScreenFailure(
                failure.Folder,
                $"{Describe(failure.Stage)}: {failure.Message}")));
        }
        return failures;
    }

    public void RequestRediscovery()
    {
        if (_rediscover is null)
        {
            // Honest rather than silent: the button exists, the searcher does
            // not yet, and the log says which.
            Citadel.Core.Log.Modules("[Settings] rediscovery requested, but no searcher is installed yet");
            return;
        }
        _rediscover();
    }

    public AppUpdateState UpdateState() => _updateController.Snapshot;

    public void CheckForUpdates() => _updateController.Check();

    public void InstallUpdate() => _updateController.Install();

    public void OpenSettings(string route)
    {
        var definition = PopupRoute(route)
            ?? throw new ArgumentException($"unknown settings screen '{route}'", nameof(route));
        var owner = _owner()
            ?? throw new InvalidOperationException("the main window is not available");

        var window = _settingsWindow;
        if (window is null)
        {
            window = new SettingsWindow(owner);
            window.Closed += OnSettingsWindowClosed;
            _settingsWindow = window;
        }

        if (!window.TryShowRoute(route, definition)) return;

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Test seam for the one reusable editor window.</summary>
    internal SettingsWindow? OpenWindow => _settingsWindow;

    internal void CloseWindow() => _settingsWindow?.Close();

    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        _gate.RegistryChanged -= OnGateChanged;
        _gate.RegistrationRefused -= OnRefused;
        _updateController.Changed -= OnUpdateChanged;
        _updateController.Dispose();

        var window = _settingsWindow;
        _settingsWindow = null;
        if (window is not null)
        {
            window.Closed -= OnSettingsWindowClosed;
            window.Close();
        }
    }

    private void OnGateChanged() => Changed?.Invoke();

    private void OnRefused(RegistrationFailure failure) => Changed?.Invoke();

    private void OnUpdateChanged() => Changed?.Invoke();

    private void OnSettingsWindowClosed(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(_settingsWindow, sender)) return;
        _settingsWindow!.Closed -= OnSettingsWindowClosed;
        _settingsWindow = null;
    }

    private BuiltInRoute? PopupRoute(string route) => route switch
    {
        SettingsScreen.AppearanceRoute => new BuiltInRoute(
            "Appearance",
            lifetime => new AppearanceScreen(_tokens, lifetime)),
        SettingsScreen.LayoutRoute => new BuiltInRoute(
            "Module layout",
            lifetime => new ModuleLayoutScreen(
                this, _tokens, SettingsLayout.Declaration(), lifetime)),
        SettingsScreen.GalleryRoute => new BuiltInRoute(
            "Gallery",
            lifetime => new GalleryScreen(this, lifetime)),
        _ => null,
    };

    private static string Describe(RegistrationRefusal reason) => reason switch
    {
        RegistrationRefusal.DuplicateRoute => "duplicate route",
        RegistrationRefusal.ReservedRoute => "reserved route",
        _ => "view failed",
    };

    private static string Describe(SearchStage stage) => stage switch
    {
        SearchStage.Root => "module folder",
        SearchStage.Manifest => "module.json",
        SearchStage.Layout => "layout.json",
        SearchStage.Entry => "entry assembly",
        _ => "module type",
    };
}
