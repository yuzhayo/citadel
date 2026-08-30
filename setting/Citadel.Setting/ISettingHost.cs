using Citadel.Core.Modules;

namespace Citadel.Setting;

/// <summary>
/// One failed screen, as Settings shows it. v0 wrote load failures to
/// Console.Error (ModuleLoader.cs:54) and surfaced nothing. Citadel keeps the
/// failure visible beside the Update action.
/// </summary>
public sealed record ScreenFailure(string Source, string Message);

/// <summary>
/// The updater state Settings renders. Network and package details deliberately
/// stay out of this type: Shell owns Velopack and only publishes UI-ready state.
/// </summary>
public sealed record AppUpdateState(
    string CurrentVersion,
    string AvailableVersion,
    string Status,
    int Progress,
    bool Supported,
    bool CanCheck,
    bool CanInstall,
    bool Busy,
    bool Available);

/// <summary>
/// Everything Settings needs to know about the outside world, defined here
/// rather than in Shell.
///
/// This is the seam that keeps the dependency graph honest: Settings must not
/// reference Shell or the searcher, but it does need the registry,
/// the failure list, and a way to ask for rediscovery. So it declares the shape
/// and App fills it in. v0 passed manifests through the SettingsPage constructor
/// (MainWindow.xaml.cs:212), which cannot survive discovery arriving at runtime.
/// </summary>
public interface ISettingHost
{
    /// <summary>Installed screens, newest snapshot. Empty is normal.</summary>
    IReadOnlyList<ModuleDescriptor> Screens();

    /// <summary>Everything that failed to load or to show, for the UI list.</summary>
    IReadOnlyList<ScreenFailure> Failures();

    /// <summary>Ask the module searcher to rediscover citizen folders.</summary>
    void RequestRediscovery();

    /// <summary>Newest application-update state, supplied by Shell.</summary>
    AppUpdateState UpdateState();

    /// <summary>Check the public Citadel GitHub Releases feed without blocking UI.</summary>
    void CheckForUpdates();

    /// <summary>Download the discovered release, apply it, and restart Citadel.</summary>
    void InstallUpdate();

    /// <summary>
    /// Open one of Settings' reserved sub-screens in the Shell-owned settings
    /// window. The editor stays outside the shell surface it previews.
    /// </summary>
    void OpenSettings(string route);

    /// <summary>Raised on the main thread when Screens/Failures changed.</summary>
    event Action? Changed;
}
