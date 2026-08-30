using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;

namespace Citadel.Setting.Screens;

/// <summary>
/// The route `settings`: what is installed, what failed, and the way into the
/// three sub-screens hosted by Shell's separate settings window.
///
/// Two things differ from v0 deliberately. It reads the registry through the
/// host seam rather than taking manifests through its constructor
/// (MainWindow.xaml.cs:212) — a constructor argument cannot survive discovery
/// arriving at runtime. And it **surfaces load failures**, which v0 wrote to
/// Console.Error (ModuleLoader.cs:54); without this the Update button "tells you
/// nothing".
///
/// With an empty module/ the screen is still fully usable.
/// </summary>
public sealed class SettingsScreen : SettingScreen
{
    public const string AppearanceRoute = "settings/appearance";
    public const string LayoutRoute = "settings/layout";
    public const string GalleryRoute = "settings/gallery";

    private readonly ISettingHost _host;
    private readonly SettingTable _screens = new();
    private readonly StackPanel _failures = new();
    private readonly TextBlock _summary = Body(string.Empty);
    private readonly TextBlock _updateVersion = Body(string.Empty);
    private readonly TextBlock _updateStatus = Body(string.Empty);
    private readonly SettingButton _checkUpdates;
    private readonly SettingButton _installUpdate;

    public SettingsScreen(ISettingHost host, Lifetime lifetime)
        : base(lifetime)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        AutomationProperties.SetAutomationId(this, "SettingsScreen");
        AutomationProperties.SetAutomationId(_screens, "ScreenList");
        AutomationProperties.SetAutomationId(_failures, "FailureList");
        AutomationProperties.SetAutomationId(_summary, "ScreenSummary");

        _screens.SetColumns(["Title", "Route", "Order"]);
        _screens.SortColumn = "Order";

        _checkUpdates = Action(
            "Check now",
            "CheckUpdates",
            _host.CheckForUpdates);
        _installUpdate = Action(
            "Update & restart",
            "InstallUpdate",
            _host.InstallUpdate);
        _updateStatus.Margin = new Thickness(0, 4, 0, 12);

        Add(Section("SCREENS"));
        Add(LayoutSlot(Card(Stack(_summary, _screens), "ScreensCard"), "screens"));

        Add(Section("PROBLEMS"));
        Add(LayoutSlot(Card(_failures, "FailuresCard"), "problems"));

        Add(Section("CUSTOMISE"));
        Add(LayoutSlot(Card(Row(
            Action("Appearance", "OpenAppearance", () => _host.OpenSettings(AppearanceRoute)),
            Action("Module layout", "OpenLayout", () => _host.OpenSettings(LayoutRoute)),
            Action("Gallery", "OpenGallery", () => _host.OpenSettings(GalleryRoute))),
            "CustomiseCard"), "actions"));

        Add(Section("SCREEN FOLDER"));
        Add(Card(Stack(
            Body("Citadel finds screens by watching the module folder. Update rescans it now."),
            Row(Action("Update modules", "UpdateModules", RequestRediscovery))),
            "UpdateCard"));

        Add(Section("UPDATES"));
        Add(Card(Stack(
            _updateVersion,
            _updateStatus,
            Row(_checkUpdates, _installUpdate)),
            "AppUpdateCard"));

        Refresh();
        _host.Changed += Refresh;
        Lifetime.Add(() => _host.Changed -= Refresh);
    }

    /// <summary>Test seam: what the failure list is showing.</summary>
    internal IReadOnlyList<string> VisibleFailures =>
        [.. _failures.Children.OfType<TextBlock>().Select(block => block.Text)];

    internal SettingTable ScreenTable => _screens;

    internal string VisibleUpdateVersion => _updateVersion.Text;

    internal string VisibleUpdateStatus => _updateStatus.Text;

    internal bool CheckUpdatesEnabled => _checkUpdates.IsEnabled;

    internal Visibility InstallUpdateVisibility => _installUpdate.Visibility;

    /// <summary>Test seam: press one of the screen's buttons by AutomationId.</summary>
    internal void Click(string automationId)
    {
        var button = Descendants<SettingButton>(this)
            .FirstOrDefault(candidate =>
                AutomationProperties.GetAutomationId(candidate) == automationId)
            ?? throw new InvalidOperationException($"no button '{automationId}'");
        button.RaiseEvent(new RoutedEventArgs(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;
            foreach (var descendant in Descendants<T>(node)) yield return descendant;
        }
    }

    private void RequestRediscovery()
    {
        // The host owns the searcher seam so this button never needs direct
        // filesystem knowledge.
        _host.RequestRediscovery();
        Refresh();
    }

    private void Refresh()
    {
        var update = _host.UpdateState();
        _updateVersion.Text = $"Version {update.CurrentVersion}";
        _updateStatus.Text = update.Status;
        _checkUpdates.IsEnabled = update.CanCheck && !update.Busy;
        _installUpdate.IsEnabled = update.CanInstall && !update.Busy;
        _installUpdate.Visibility = update.Available
            ? Visibility.Visible
            : Visibility.Collapsed;

        var screens = _host.Screens();
        _screens.SetRows(screens
            .Select(descriptor => (IReadOnlyList<string>)
            [
                descriptor.Title,
                descriptor.Route,
                descriptor.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ])
            .ToList());

        _summary.Text = screens.Count switch
        {
            0 => "No screens installed. Citadel runs fine without any — drop a folder into module/ to add one.",
            1 => "1 screen installed.",
            _ => $"{screens.Count} screens installed.",
        };

        _failures.Children.Clear();
        var failures = _host.Failures();
        if (failures.Count == 0)
        {
            _failures.Children.Add(Body("Nothing failed."));
            return;
        }

        foreach (var failure in failures)
        {
            var line = Body($"{failure.Source} — {failure.Message}");
            line.Margin = new Thickness(0, 0, 0, 4);
            _failures.Children.Add(line);
        }
    }
}
