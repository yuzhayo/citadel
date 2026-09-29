using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Citadel.Setting.Components;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Shortcuts;

/// <summary>One checkbox line: a CamoProf profile plus whether Agentrouter points at it.</summary>
internal sealed class SelectableProfileRow : INotifyPropertyChanged
{
    private bool _isAdded;

    public SelectableProfileRow(string profileId, string account, bool isAdded)
    {
        ProfileId = profileId;
        Account = account;
        _isAdded = isAdded;
    }

    /// <summary>Profile folder name — the key stored in shortcuts.json.</summary>
    public string ProfileId { get; }

    /// <summary>Gmail address, or the profile folder name when none is saved.</summary>
    public string Account { get; }

    /// <summary>True when this screen has a pointer at the profile.</summary>
    public bool IsAdded
    {
        get => _isAdded;
        set
        {
            if (_isAdded == value)
            {
                return;
            }

            _isAdded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The floating screen behind Select profiles…: every CamoProf profile with a
/// checkbox. Checked = Agentrouter points at it, unchecked = pointer dropped,
/// and every toggle is written straight to the shortcut store so the Launcher
/// table behind it is already correct when this closes. Selection lives here;
/// the Launcher content area only lists what survived it.
/// </summary>
public sealed partial class SelectProfilesDialog : SettingDialog
{
    private readonly ShortcutCatalog _catalog;
    private readonly ObservableCollection<SelectableProfileRow> _rows = [];
    private bool _loading;

    internal SelectProfilesDialog(ShortcutCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        InitializeComponent();
        ProfileTable.ItemsSource = _rows;
        Load();
    }

    private void Load()
    {
        var available = _catalog.ScanAvailable().ToList();
        var added = _catalog.Load()
            .Select(entry => entry.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A pointer whose folder has vanished stays listed and checked, so it
        // can be unchecked here instead of silently disappearing from view.
        var ids = available
            .Concat(added.Where(id => !available.Contains(id, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _loading = true;
        try
        {
            _rows.Clear();
            foreach (var id in ids)
            {
                var row = new SelectableProfileRow(
                    id,
                    ProfileIdentity.DisplayName(id),
                    added.Contains(id));
                row.PropertyChanged += Row_PropertyChanged;
                _rows.Add(row);
            }
        }
        finally
        {
            _loading = false;
        }

        WriteSummary();
        EmptyText.Visibility = _rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading
            || e.PropertyName != nameof(SelectableProfileRow.IsAdded)
            || sender is not SelectableProfileRow row)
        {
            return;
        }

        try
        {
            if (row.IsAdded)
            {
                _catalog.Add(row.ProfileId);
            }
            else
            {
                _catalog.Remove(row.ProfileId);
            }

            SetStatus(null);
            WriteSummary();
        }
        catch (Exception ex)
        {
            // The store refused, so the checkbox must go back to reality.
            _loading = true;
            try
            {
                row.IsAdded = !row.IsAdded;
            }
            finally
            {
                _loading = false;
            }

            SetStatus("Update failed: " + ex.Message);
        }
    }

    private void WriteSummary()
    {
        var count = _rows.Count(row => row.IsAdded);
        SummaryText.Text = count == 0
            ? "No profiles added."
            : $"{count} profile{(count == 1 ? "" : "s")} added to Agentrouter.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
