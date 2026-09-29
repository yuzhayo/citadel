using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Shortcuts;

/// <summary>
/// The Launcher tab: lists the CamoProf profiles Agentrouter already points at,
/// keyed by profile folder but shown by Gmail address. Selecting which ones
/// are pointed at happens in the Select profiles… floating screen; this view
/// only renders the result and can drop a pointer.
/// </summary>
public partial class ShortcutsView : UserControl, IDisposable
{
    private readonly ShortcutCatalog _catalog;
    private readonly ObservableCollection<ShortcutRow> _rows = [];
    private bool _disposed;

    internal ShortcutsView(ShortcutCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        InitializeComponent();
        ShortcutTable.ItemsSource = _rows;
        Refresh();
    }

    /// <summary>Rebuilds the table from the stored shortcuts only.</summary>
    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var available = _catalog.ScanAvailable()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shortcuts = _catalog.Load();

            _rows.Clear();
            foreach (var entry in shortcuts)
            {
                _rows.Add(new ShortcutRow(
                    entry.ProfileId,
                    available.Contains(entry.ProfileId),
                    entry.AddedAtUtc));
            }

            var count = _rows.Count;
            var missing = _rows.Count(row => row.Status == "Missing");
            SummaryText.Text = count == 0
                ? "No profiles added."
                : $"{count} profile{(count == 1 ? "" : "s")} added"
                  + (missing > 0 ? $" · {missing} missing" : string.Empty);

            EmptyText.Visibility = count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SetStatus("Shortcut refresh failed: " + ex.Message);
        }
    }

    private void SelectProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            SetStatus(null);
            var dialog = new SelectProfilesDialog(_catalog)
            {
                Owner = Window.GetWindow(this),
            };
            dialog.ShowDialog();
            Refresh();
        }
        catch (Exception ex)
        {
            SetStatus("Select profiles failed: " + ex.Message);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        SetStatus(null);
        Refresh();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement { Tag: ShortcutRow row })
        {
            return;
        }

        try
        {
            _catalog.Remove(row.ProfileId);
            SetStatus(
                "Shortcut to '" + row.Account + "' removed. "
                + "The CamoProf profile itself was not touched.");
            Refresh();
        }
        catch (Exception ex)
        {
            SetStatus("Shortcut update failed: " + ex.Message);
        }
    }

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
