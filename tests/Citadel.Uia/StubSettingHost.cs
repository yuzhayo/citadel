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
    private readonly List<SidebarGroup> _sidebarGroups = [];
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

    public event Action? SidebarGroupsChanged;

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

    public IReadOnlyList<SidebarGroup> SidebarGroups() => [.. _sidebarGroups];

    public string CreateSidebarGroup(string name)
    {
        var group = new SidebarGroup(Guid.NewGuid().ToString("N"), name.Trim(), true, []);
        _sidebarGroups.Add(group);
        SidebarGroupsChanged?.Invoke();
        Changed?.Invoke();
        return group.Id;
    }

    public void RenameSidebarGroup(string id, string name)
    {
        var index = _sidebarGroups.FindIndex(group => group.Id == id);
        _sidebarGroups[index] = _sidebarGroups[index] with { Name = name.Trim() };
        SidebarGroupsChanged?.Invoke();
        Changed?.Invoke();
    }

    public void DeleteSidebarGroup(string id)
    {
        _sidebarGroups.RemoveAll(group => group.Id == id);
        SidebarGroupsChanged?.Invoke();
        Changed?.Invoke();
    }

    public void SetSidebarGroupMembership(string id, string route, bool included)
    {
        for (var index = 0; index < _sidebarGroups.Count; index++)
        {
            var routes = _sidebarGroups[index].Routes
                .Where(item => !string.Equals(item, route, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (_sidebarGroups[index].Id == id && included) routes.Add(route);
            _sidebarGroups[index] = _sidebarGroups[index] with { Routes = routes };
        }
        SidebarGroupsChanged?.Invoke();
        Changed?.Invoke();
    }

    public void SetSidebarGroupExpanded(string id, bool expanded)
    {
        var index = _sidebarGroups.FindIndex(group => group.Id == id);
        _sidebarGroups[index] = _sidebarGroups[index] with { Expanded = expanded };
        SidebarGroupsChanged?.Invoke();
        Changed?.Invoke();
    }

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
