using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Setting.Components;

namespace Module.Mangareader.Library.Grouping;

/// <summary>
/// Presentation for Library's Grouping feature. It renders the shared tab strip,
/// opens the shared dialog for the two grouping commands, and reports the
/// feature's own warnings; every rule about names, membership and persistence
/// stays in <see cref="GroupingFeature"/>.
/// </summary>
public partial class GroupingBar : UserControl
{
    /// <summary>Sentinel target meaning "create a new group" in the dialog.</summary>
    private const string NewGroupTarget = "__new-group__";

    private GroupingFeature? _feature;
    private Func<IReadOnlyList<string>>? _loadedTitles;
    private TabItem? _addGroupTab;
    private TabItem? _allTab;
    private bool _rendering;

    public GroupingBar() => InitializeComponent();

    /// <summary>
    /// Attaches the feature and the pull-based source of currently loaded title
    /// folder names. Pulling keeps the dialog's title list and the unavailable
    /// count honest after a re-scan without this bar holding a Library snapshot.
    /// </summary>
    public void UseGrouping(
        GroupingFeature feature,
        Func<IReadOnlyList<string>> loadedTitleFolderNames)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(loadedTitleFolderNames);
        if (ReferenceEquals(_feature, feature)) return;

        if (_feature is not null) _feature.Changed -= Feature_Changed;
        _feature = feature;
        _loadedTitles = loadedTitleFolderNames;
        _feature.Changed += Feature_Changed;
        Render();
    }

    /// <summary>
    /// Places Grouping's own `Add Group` action into a reserved slot of the
    /// local title detail. Grouping owns the button and its dialog; the caller
    /// only supplies the slot and a pull of the currently active title.
    /// </summary>
    public void InstallAddTitleAction(Panel slot, Func<string?> activeTitleFolderName)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(activeTitleFolderName);

        var button = new SettingButton
        {
            Content = "Add Group",
            MinWidth = 104,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, "Add this title to a group");
        button.Click += (_, _) => ShowGroupDialog(activeTitleFolderName());
        slot.Children.Add(button);
    }

    private void Feature_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Render);
            return;
        }

        Render();
    }

    private void Render()
    {
        if (_feature is null || _rendering) return;

        _rendering = true;
        try
        {
            GroupTabs.Items.Clear();

            // Leftmost tab is the create action; it is never a page.
            _addGroupTab = new TabItem { Header = "Add Group" };
            AutomationProperties.SetName(_addGroupTab, "Add a new group");
            GroupTabs.Items.Add(_addGroupTab);

            // Recorded deviation from SPEC section 9, documented there: the shared
            // tab component always has a selected tab, so this is the only way back
            // to an unfiltered view. It is not a group — it stores nothing, has no
            // membership and is never persisted; selecting it just clears the filter.
            _allTab = new TabItem { Header = "All titles" };
            AutomationProperties.SetName(_allTab, "Show all titles, no group filter");
            GroupTabs.Items.Add(_allTab);

            foreach (var group in _feature.Groups)
            {
                GroupTabs.Items.Add(new TabItem { Header = group.Name, Tag = group.Id });
            }

            GroupTabs.SelectedItem = TabForGroup(_feature.ActiveGroupId);
        }
        finally
        {
            _rendering = false;
        }

        RenderStatus();
    }

    private void RenderStatus()
    {
        if (_feature is null || _loadedTitles is null) return;

        var unavailable = _feature.UnavailableTitles(_loadedTitles());
        var message = unavailable.Count == 0
            ? _feature.LastWarning
            : $"{unavailable.Count} recorded title(s) in this group are not in the library right now: "
              + string.Join(", ", unavailable.Take(5))
              + (unavailable.Count > 5 ? ", …" : string.Empty)
              + (_feature.LastWarning is { } warning ? $" — {warning}" : string.Empty);

        GroupStatusText.Text = message ?? string.Empty;
        GroupStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void GroupTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || _feature is null) return;
        if (GroupTabs.SelectedItem is not TabItem selected) return;

        if (!ReferenceEquals(selected, _addGroupTab))
        {
            _feature.SelectGroup(selected.Tag as string);
            return;
        }

        // An action tab must not become the selection: ask for the group, then
        // put the strip back on whatever filter was actually active.
        var restore = TabForGroup(_feature.ActiveGroupId);
        ShowGroupDialog(activeTitleFolderName: null);
        _rendering = true;
        try
        {
            GroupTabs.SelectedItem = restore;
        }
        finally
        {
            _rendering = false;
        }
    }

    private TabItem? TabForGroup(string? groupId)
    {
        if (groupId is null) return _allTab;
        return GroupTabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => Equals(item.Tag, groupId)) ?? _allTab;
    }

    /// <summary>
    /// One shared dialog for both grouping commands. Without an active title it
    /// creates a group and may optionally include loaded titles; with one it
    /// adds that title to a chosen existing group or to a newly created one.
    /// </summary>
    private void ShowGroupDialog(string? activeTitleFolderName)
    {
        if (_feature is null) return;

        var addingTitle = !string.IsNullOrWhiteSpace(activeTitleFolderName);
        var dialog = new SettingDialog
        {
            Title = addingTitle ? "Add title to group" : "Add group",
            Width = 480,
        };
        if (Window.GetWindow(this) is { } owner) dialog.Owner = owner;

        var root = new StackPanel();

        ComboBox? targetPicker = null;
        if (addingTitle)
        {
            targetPicker = new ComboBox
            {
                Height = 38,
                DisplayMemberPath = "Name",
                Margin = new Thickness(0, 4, 0, 0),
            };
            targetPicker.SetResourceReference(StyleProperty, "SettingComboBoxStyle");
            AutomationProperties.SetName(targetPicker, "Destination group");

            var targets = new List<GroupTarget> { new(NewGroupTarget, "New group…") };
            targets.AddRange(_feature.Groups.Select(group => new GroupTarget(group.Id, group.Name)));
            targetPicker.ItemsSource = targets;
            targetPicker.SelectedItem = _feature.ActiveGroupId is { } active
                ? targets.FirstOrDefault(target => target.Id == active) ?? targets[0]
                : targets[0];

            root.Children.Add(Label("Add to"));
            root.Children.Add(targetPicker);
            root.Children.Add(Body($"Title: {activeTitleFolderName}"));
        }

        var nameField = new SettingField
        {
            Height = 38,
            Placeholder = "Group name (required for a new group)",
            Margin = new Thickness(0, 4, 0, 0),
        };
        AutomationProperties.SetName(nameField, "Group name");
        root.Children.Add(Label("Group name"));
        root.Children.Add(nameField);

        ListBox? titleList = null;
        if (!addingTitle)
        {
            titleList = new ListBox
            {
                MaxHeight = 160,
                SelectionMode = SelectionMode.Extended,
                Margin = new Thickness(0, 4, 0, 0),
                ItemsSource = _loadedTitles?.Invoke() ?? [],
            };
            titleList.SetResourceReference(StyleProperty, "SettingListStyle");
            AutomationProperties.SetName(titleList, "Titles to include");
            root.Children.Add(Label("Titles (optional — an empty group is valid)"));
            root.Children.Add(titleList);
        }

        var errorText = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        errorText.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        root.Children.Add(errorText);

        var accept = new SettingButton
        {
            Content = addingTitle ? "Add title" : "Create group",
            MinWidth = 108,
        };
        var cancel = new SettingButton
        {
            Content = "Cancel",
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;

        accept.Click += (_, _) =>
        {
            var target = targetPicker?.SelectedItem as GroupTarget;
            var outcome = target is null || target.Id == NewGroupTarget
                ? _feature.CreateGroup(nameField.Text, SelectedTitles(titleList, activeTitleFolderName))
                : _feature.AddTitleToGroup(target.Id, activeTitleFolderName!);

            if (outcome.Error is not null)
            {
                errorText.Text = outcome.Error;
                errorText.Visibility = Visibility.Visible;
                return;
            }

            dialog.DialogResult = true;
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        actions.Children.Add(accept);
        actions.Children.Add(cancel);
        root.Children.Add(actions);

        // The name is only meaningful when a new group is being created.
        if (targetPicker is not null)
        {
            targetPicker.SelectionChanged += (_, _) =>
                nameField.IsEnabled = targetPicker.SelectedItem is GroupTarget target
                    && target.Id == NewGroupTarget;
            nameField.IsEnabled = targetPicker.SelectedItem is GroupTarget initial
                && initial.Id == NewGroupTarget;
        }

        dialog.Content = root;
        dialog.ShowDialog();
        RenderStatus();
    }

    private static IReadOnlyList<string> SelectedTitles(ListBox? list, string? activeTitle)
    {
        var titles = list?.SelectedItems.OfType<string>().ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(activeTitle)
            && !titles.Contains(activeTitle, StringComparer.OrdinalIgnoreCase))
        {
            titles.Add(activeTitle);
        }

        return titles;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        block.SetResourceReference(TextBlock.FontSizeProperty, "SettingSectionFontSize");
        return block;
    }

    private static TextBlock Body(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Body");
        return block;
    }

    private sealed record GroupTarget(string Id, string Name);
}
