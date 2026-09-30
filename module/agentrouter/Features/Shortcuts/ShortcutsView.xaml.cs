using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Shortcuts;

/// <summary>
/// The Launcher tab: lists the CamoProf profiles Agentrouter already points at,
/// keyed by profile folder but shown by Gmail address. Selecting which ones
/// are pointed at happens in the Select profiles… floating screen; this view
/// renders the result, offers the committed proxy pool as choices, and can
/// drop a pointer. Proxy choice is view state only; it does not route traffic.
/// </summary>
public partial class ShortcutsView : UserControl, IDisposable
{
    private readonly ShortcutCatalog _catalog;
    private readonly AgentProxyPool _pool;
    private readonly ObservableCollection<ShortcutRow> _rows = [];
    private bool _disposed;

    internal ShortcutsView(ShortcutCatalog catalog, AgentProxyPool pool)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        InitializeComponent();
        ShortcutTable.ItemsSource = _rows;
        Refresh();
    }

    /// <summary>Rebuilds shortcuts and choices from the committed combined pool.</summary>
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
            var selections = _rows.ToDictionary(row => row.ProfileId,
                row => row.SelectedProxy, StringComparer.OrdinalIgnoreCase);
            _pool.Reload();
            var proxyChoices = new[] { "Random" }
                .Concat(_pool.Rows.Select(row => row.Endpoint.Canonical))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            _rows.Clear();
            foreach (var entry in shortcuts)
            {
                _rows.Add(new ShortcutRow(
                    entry.ProfileId,
                    available.Contains(entry.ProfileId),
                    entry.AddedAtUtc,
                    proxyChoices,
                    selections.TryGetValue(entry.ProfileId, out var selected)
                        && proxyChoices.Contains(selected, StringComparer.Ordinal)
                            ? selected : "Random"));
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
