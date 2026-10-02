using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Module.Agentrouter.Features.Claim;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Shortcuts;

/// <summary>
/// The Launcher tab: lists the CamoProf profiles Agentrouter already points at,
/// keyed by profile folder but shown by Gmail address. Selecting which ones
/// are pointed at happens in the Select profiles… floating screen; this view
/// renders the result, offers the committed proxy pool as choices, and passes
/// the selected route into the claim flow.
/// Each row also shows what the profile's run JSON holds — the account id a
/// claim recorded, the api_key that claim captured, and the last balance Check
/// balance fetched. This view reads those, it never invents them; the key is
/// shown and copied, never rewritten.
/// </summary>
public partial class ShortcutsView : UserControl, IDisposable
{
    private readonly ShortcutCatalog _catalog;
    private readonly AgentProxyPool _pool;
    private readonly IAgentRouterClaimService _claims;
    private readonly ObservableCollection<ShortcutRow> _rows = [];
    private bool _disposed;

    internal ShortcutsView(ShortcutCatalog catalog, AgentProxyPool pool,
        IAgentRouterClaimService claims)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _claims = claims ?? throw new ArgumentNullException(nameof(claims));
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
            var proxyChoices = new[] { "No proxy", "Random" }
                .Concat(_pool.Rows.Select(row => row.Endpoint.Canonical))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            _rows.Clear();
            foreach (var entry in shortcuts)
            {
                // The run JSON is this row's source of truth: the id a claim
                // recorded, and the last balance a check stored. Both are read
                // from disk on every refresh, so a number survives a restart
                // and changes only when a new check writes it.
                var state = _claims.Read(entry.ProfileId);
                _rows.Add(new ShortcutRow(
                    entry.ProfileId,
                    available.Contains(entry.ProfileId),
                    entry.AddedAtUtc,
                    proxyChoices,
                    selections.TryGetValue(entry.ProfileId, out var selected)
                        && proxyChoices.Contains(selected, StringComparer.Ordinal)
                            ? selected : "No proxy")
                {
                    UserId = state.UserId,
                    ApiKey = state.ApiKey,
                    Balance = state.Balance,
                });
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

    private void AutoClaimButton_Click(object sender, RoutedEventArgs e)
    {
        // Placeholder: the claim pipeline is not wired yet.
        if (_disposed)
        {
            return;
        }

        SetStatus("Auto claim is not wired up yet.");
    }

    /// <summary>
    /// Runs the claim flow for one row. The handler only adapts the UI event —
    /// the flow itself lives behind the citizen's pyhost command.
    /// </summary>
    private async void ClaimButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement element
            || element.Tag is not ShortcutRow row)
        {
            return;
        }

        // Literal: ON shows the browser, OFF hides it. Nothing flips it once
        // the run has started.
        var headless = ShowBrowserToggle.IsChecked != true;
        var proxy = ResolveProxy(row.SelectedProxy);

        // The panel PAT is single-reveal, so a second click while one claim is
        // running must not be able to race the first on the same profile.
        element.IsEnabled = false;
        SetStatus($"Claiming {row.Account}…");
        try
        {
            var result = await _claims.ClaimAsync(row.ProfileId, headless, proxy);
            SetStatus(result.Describe());
        }
        catch (Exception ex)
        {
            // The flow may already have mutated the account before it failed;
            // reporting a plain failure would invite a destructive retry.
            SetStatus("Claim outcome unknown (" + ex.Message
                      + "). Verify the account before claiming again.");
        }
        finally
        {
            element.IsEnabled = true;
        }
    }

    private string? ResolveProxy(string selection)
    {
        if (selection.Equals("No proxy", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (selection.Equals("Random", StringComparison.OrdinalIgnoreCase))
        {
            var rows = _pool.Rows;
            return rows.Count == 0
                ? null
                : rows[Random.Shared.Next(rows.Count)].Endpoint.Canonical;
        }

        return selection;
    }

    /// <summary>
    /// Fetches one row's balance and stores it in the profile's run JSON.
    ///
    /// <para>Clicking is the only thing that changes the number, and a failed
    /// fetch leaves the stored one alone: nothing here blanks a balance it
    /// could not re-read.</para>
    /// </summary>
    private async void CheckBalanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement element
            || element.Tag is not ShortcutRow row)
        {
            return;
        }

        element.IsEnabled = false;
        SetStatus($"Checking balance for {row.Account}…");
        try
        {
            var outcome = await _claims.CheckAsync(row.ProfileId);
            SetStatus($"{row.Account}: {outcome.Describe()}");
        }
        catch (Exception ex)
        {
            SetStatus("Balance check failed: " + ex.Message);
        }
        finally
        {
            element.IsEnabled = true;
        }

        // Re-reading the JSON is what puts a saved number on screen. This view
        // holds no balance state of its own.
        Refresh();
    }

    /// <summary>
    /// Copies one row's captured api_key to the clipboard. The key comes from
    /// the profile's run JSON — this view never invents one, and a row without a
    /// key has its Copy button disabled, so the empty branch only fires for a
    /// keyboard or automation path past that.
    /// </summary>
    private void CopyKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement { Tag: ShortcutRow row })
        {
            return;
        }

        if (!row.HasApiKey)
        {
            SetStatus($"{row.Account}: no API key captured yet — Claim it first.");
            return;
        }

        try
        {
            Clipboard.SetText(row.ApiKey);
            SetStatus($"{row.Account}: API key copied to the clipboard.");
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource: another process holding it
            // open must not take the window down.
            SetStatus("Copy failed: " + ex.Message);
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
