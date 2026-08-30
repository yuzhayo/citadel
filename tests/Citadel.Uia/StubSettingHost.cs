using Citadel.Core.Modules;
using Citadel.Setting;

namespace Citadel.Uia;

/// <summary>
/// A Settings host the test drives directly. It exists because Settings declares
/// the seam and Shell fills it — so a test can fill it too, with no gate, no
/// searcher, and no filesystem.
/// </summary>
internal sealed class StubSettingHost : ISettingHost
{
    private readonly List<ModuleDescriptor> _screens = [];
    private readonly List<ScreenFailure> _failures = [];
    private AppUpdateState _updateState = new(
        "0.1.0",
        string.Empty,
        "Updates are available after Citadel is installed.",
        0,
        false,
        false,
        false,
        false,
        false);

    public event Action? Changed;

    public int RediscoveryRequests { get; private set; }

    public int UpdateChecks { get; private set; }

    public int UpdateInstalls { get; private set; }

    public List<string> OpenedSettings { get; } = [];

    public IReadOnlyList<ModuleDescriptor> Screens() => _screens;

    public IReadOnlyList<ScreenFailure> Failures() => _failures;

    public void RequestRediscovery() => RediscoveryRequests++;

    public AppUpdateState UpdateState() => _updateState;

    public void CheckForUpdates() => UpdateChecks++;

    public void InstallUpdate() => UpdateInstalls++;

    public void OpenSettings(string route) => OpenedSettings.Add(route);

    public void SetScreens(params ModuleDescriptor[] screens)
    {
        _screens.Clear();
        _screens.AddRange(screens);
        Changed?.Invoke();
    }

    public void SetFailures(params ScreenFailure[] failures)
    {
        _failures.Clear();
        _failures.AddRange(failures);
        Changed?.Invoke();
    }

    public void SetUpdateState(AppUpdateState state)
    {
        _updateState = state;
        Changed?.Invoke();
    }
}
