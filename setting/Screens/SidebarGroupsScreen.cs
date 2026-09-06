using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;

namespace Citadel.Setting.Screens;

/// <summary>Edits Shell-owned sidebar presentation groups through the host seam.</summary>
public sealed class SidebarGroupsScreen : SettingScreen
{
    private readonly ISettingHost _host;
    private readonly ListBox _groups = new() { DisplayMemberPath = nameof(SidebarGroup.Name), MinHeight = 120 };
    private readonly SettingField _name = new() { Placeholder = "Group name", Width = 240 };
    private readonly StackPanel _modules = new();
    private readonly TextBlock _status = Body(string.Empty);
    private string? _selectedId;
    private bool _refreshing;

    public SidebarGroupsScreen(ISettingHost host, Lifetime lifetime)
        : base(lifetime)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        AutomationProperties.SetAutomationId(this, "SidebarGroupsScreen");
        AutomationProperties.SetAutomationId(_groups, "SidebarGroupList");
        AutomationProperties.SetAutomationId(_name, "SidebarGroupName");
        AutomationProperties.SetAutomationId(_modules, "SidebarGroupModules");
        AutomationProperties.SetAutomationId(_status, "SidebarGroupStatus");
        _groups.SetResourceReference(FrameworkElement.StyleProperty, "SettingListStyle");

        Add(Section("GROUPS"));
        Add(Card(Stack(
            Body("Groups change sidebar presentation only. Module routes and discovery stay unchanged."),
            _groups,
            Row(
                _name,
                Action("Add group", "AddSidebarGroup", AddGroup),
                Action("Rename", "RenameSidebarGroup", RenameGroup),
                Action("Delete", "DeleteSidebarGroup", DeleteGroup))),
            "SidebarGroupsCard"));

        Add(Section("MODULES"));
        Add(Card(Stack(
            Body("Select one group, then choose its modules. Assigning a module moves it from any previous group."),
            _modules),
            "SidebarGroupModulesCard"));
        Add(Card(_status, "SidebarGroupStatusCard"));

        _groups.SelectionChanged += OnSelectionChanged;
        _host.Changed += Refresh;
        Lifetime.Add(() =>
        {
            _groups.SelectionChanged -= OnSelectionChanged;
            _host.Changed -= Refresh;
        });
        Refresh();
    }

    internal IReadOnlyList<string> GroupNames =>
        [.. _groups.Items.OfType<SidebarGroup>().Select(group => group.Name)];

    internal IReadOnlyList<string> ModuleRoutes =>
        [.. _modules.Children.OfType<SettingToggle>().Select(toggle => (string)toggle.Tag)];

    internal string Status => _status.Text;

    internal void SelectGroup(string id) => _groups.SelectedItem = _groups.Items
        .OfType<SidebarGroup>()
        .FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.OrdinalIgnoreCase));

    internal void Add(string name)
    {
        _name.Text = name;
        AddGroup();
    }

    private void AddGroup() => Execute(() =>
    {
        _selectedId = _host.CreateSidebarGroup(_name.Text);
        _name.Text = string.Empty;
        Refresh();
        _status.Text = "Group added.";
    });

    private void RenameGroup() => Execute(() =>
    {
        var id = RequireSelection();
        _host.RenameSidebarGroup(id, _name.Text);
        _status.Text = "Group renamed.";
    });

    private void DeleteGroup() => Execute(() =>
    {
        var id = RequireSelection();
        _host.DeleteSidebarGroup(id);
        _selectedId = null;
        _name.Text = string.Empty;
        _status.Text = "Group deleted; its modules are ungrouped.";
    });

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshing) return;
        var selected = _groups.SelectedItem as SidebarGroup;
        _selectedId = selected?.Id;
        _name.Text = selected?.Name ?? string.Empty;
        RebuildModules(selected);
    }

    private void Refresh()
    {
        _refreshing = true;
        var groups = _host.SidebarGroups();
        _groups.ItemsSource = groups;
        var selected = groups.FirstOrDefault(group => string.Equals(group.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
        _groups.SelectedItem = selected;
        _refreshing = false;
        RebuildModules(selected);
    }

    private void RebuildModules(SidebarGroup? selected)
    {
        _modules.Children.Clear();
        if (selected is null)
        {
            _modules.Children.Add(Body("Select a group to edit membership."));
            return;
        }

        foreach (var screen in _host.Screens())
        {
            var toggle = new SettingToggle
            {
                Content = screen.Title,
                Tag = screen.Route,
                IsChecked = selected.Routes.Contains(screen.Route, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 4, 0, 4),
            };
            AutomationProperties.SetAutomationId(toggle, $"SidebarGroupModule:{screen.Route}");
            toggle.Click += (_, _) => Execute(() =>
                _host.SetSidebarGroupMembership(selected.Id, screen.Route, toggle.IsChecked == true));
            _modules.Children.Add(toggle);
        }

        if (_modules.Children.Count == 0) _modules.Children.Add(Body("No modules are installed."));
    }

    private string RequireSelection() => _selectedId
        ?? throw new InvalidOperationException("Select a group first.");

    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
    }
}
