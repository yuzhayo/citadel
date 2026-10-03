using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private readonly DataGridTemplateColumn? _loginColumn;
    private readonly DispatcherTimer _dateTimer;
    private string _loginHeaderDate = string.Empty;
    private bool _disposed;
    private bool _claimAllRunning;
    private bool _batchRunning;

    internal ShortcutsView(ShortcutCatalog catalog, AgentProxyPool pool,
        IAgentRouterClaimService claims)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _claims = claims ?? throw new ArgumentNullException(nameof(claims));
        InitializeComponent();
        ShortcutTable.ItemsSource = _rows;
        _loginColumn = ShortcutTable.InteractiveColumns
            .OfType<DataGridTemplateColumn>()
            .FirstOrDefault(column => string.Equals(
                column.Header?.ToString(), "Login", StringComparison.Ordinal));
        Refresh();
        _dateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dateTimer.Tick += DateTimer_Tick;
        _dateTimer.Start();
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
            _pool.Reload();
            var proxyChoices = new[] { "No proxy", "Random" }
                .Concat(_pool.Rows.Select(row => row.Endpoint.Canonical))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            _rows.Clear();
            var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (_loginColumn is not null)
            {
                _loginColumn.Header = $"Login · {today}";
            }
            _loginHeaderDate = today;
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
                    !string.IsNullOrWhiteSpace(entry.SelectedProxy)
                        && proxyChoices.Contains(entry.SelectedProxy, StringComparer.Ordinal)
                            ? entry.SelectedProxy : "No proxy")
                {
                    UserId = state.UserId,
                    ApiKey = state.ApiKey,
                    HasPat = state.HasPat,
                    Balance = state.Balance,
                    Login = state.Login,
                });
            }

            var count = _rows.Count;
            var missing = _rows.Count(row => row.Status == "Missing");
            if (!_batchRunning && string.IsNullOrWhiteSpace(StatusText.Text))
            {
                ProgressTitleText.Text = count == 0
                    ? "Agent Router · ready"
                    : $"{count} profiles ready"
                      + (missing > 0 ? $" · {missing} missing" : string.Empty);
                StatusText.Text = "Pilih operasi untuk memulai.";
            }

            EmptyText.Visibility = count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SetStatus("Shortcut refresh failed: " + ex.Message);
        }
    }

    private void DateTimer_Tick(object? sender, EventArgs e)
    {
        var today = DateTime.Now.ToString(
            "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(today, _loginHeaderDate, StringComparison.Ordinal))
        {
            Refresh();
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

    private async void AutoClaimButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _batchRunning)
        {
            return;
        }

        _claimAllRunning = true;
        var profiles = _rows.Where(row => row.Exists)
            .OrderBy(_ => Random.Shared.Next())
            .ToArray();
        BeginBatch("Claim all", profiles.Length);
        try
        {
            var headless = ShowBrowserToggle.IsChecked != true;
            var completed = 0;
            var succeeded = 0;
            var failed = 0;
            foreach (var row in profiles)
            {
                UpdateBatchProgress(completed, profiles.Length,
                    $"{completed}/{profiles.Length} selesai · mengklaim {row.Account}");
                try
                {
                    var result = await _claims.ClaimAsync(
                        row.ProfileId, headless, ResolveProxy(row.SelectedProxy));
                    if (result.Outcome == "saved") succeeded++;
                    else failed++;
                }
                catch
                {
                    // Continue the sequential batch; the row-level claim method
                    // reports an unknown outcome and the remaining rows still run.
                    failed++;
                }
                completed++;
                UpdateBatchProgress(completed, profiles.Length,
                    $"{completed}/{profiles.Length} selesai · {succeeded} berhasil · {failed} gagal");
            }

            FinishBatch("Claim all selesai",
                $"{succeeded} berhasil · {failed} gagal", profiles.Length);
        }
        finally
        {
            _claimAllRunning = false;
            if (_batchRunning)
            {
                FinishBatch("Claim all selesai", "Proses berakhir.", profiles.Length);
            }
            Refresh();
        }
    }

    private async void CheckAllBalanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        await RunCheckAllAsync("balance", row => _claims.CheckAsync(row.ProfileId));
    }

    private async void CheckAllLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        await RunCheckAllAsync("login", row => _claims.CheckLoginAsync(row.ProfileId));
    }

    private async Task RunCheckAllAsync<T>(
        string label,
        Func<ShortcutRow, Task<T>> check)
    {
        if (_disposed || _batchRunning) return;
        var profiles = _rows.Where(row => row.Exists && row.HasLoginCredentials
            && row.UserId is > 0).ToArray();
        BeginBatch($"Check all {label}", profiles.Length);
        var completed = 0;
        var succeeded = 0;
        var failed = 0;
        try
        {
            await Task.WhenAll(profiles.Select(async row =>
            {
                try
                {
                    var result = await check(row).ConfigureAwait(true);
                    var didSucceed = result switch
                    {
                        BalanceCheckOutcome balance => balance.State == "saved",
                        LoginCheckOutcome login => login.State == "saved",
                        _ => true,
                    };
                    if (didSucceed) Interlocked.Increment(ref succeeded);
                    else Interlocked.Increment(ref failed);
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }

                var done = Interlocked.Increment(ref completed);
                UpdateBatchProgress(done, profiles.Length,
                    $"{done}/{profiles.Length} selesai · {Volatile.Read(ref failed)} gagal");
            }));
            FinishBatch($"Check all {label} selesai",
                $"{succeeded} berhasil · {failed} gagal", profiles.Length);
        }
        finally
        {
            if (_batchRunning)
            {
                FinishBatch($"Check all {label} selesai", "Proses berakhir.", profiles.Length);
            }
            Refresh();
        }
    }

    private void BeginBatch(string title, int total)
    {
        _batchRunning = true;
        AutoClaimButton.IsEnabled = false;
        CheckAllBalanceButton.IsEnabled = false;
        CheckAllLoginButton.IsEnabled = false;
        ProgressTitleText.Text = total == 0 ? $"{title} · tidak ada profil" : title;
        StatusText.Text = total == 0 ? "Tidak ada profil yang memenuhi syarat." : $"0/{total} selesai";
        BatchProgressBar.Value = 0;
        BatchProgressBar.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateBatchProgress(int completed, int total, string detail)
    {
        StatusText.Text = detail;
        if (total > 0)
        {
            BatchProgressBar.Value = Math.Clamp(completed * 100d / total, 0, 100);
        }
    }

    private void FinishBatch(string title, string detail, int total)
    {
        _batchRunning = false;
        ProgressTitleText.Text = title;
        StatusText.Text = detail;
        BatchProgressBar.Value = total > 0 ? 100 : 0;
        BatchProgressBar.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoClaimButton.IsEnabled = true;
        CheckAllBalanceButton.IsEnabled = true;
        CheckAllLoginButton.IsEnabled = true;
    }

    /// <summary>
    /// Runs the claim flow for one row. The handler only adapts the UI event —
    /// the flow itself lives behind the citizen's pyhost command.
    /// </summary>
    private async void ClaimButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _claimAllRunning || sender is not FrameworkElement element
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

    private async void CheckLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement element
            || element.Tag is not ShortcutRow row)
        {
            return;
        }

        element.IsEnabled = false;
        SetStatus($"Checking login for {row.Account}…");
        try
        {
            var outcome = await _claims.CheckLoginAsync(row.ProfileId);
            SetStatus($"{row.Account}: {outcome.Describe()}");
        }
        catch (Exception ex)
        {
            SetStatus("Login check failed: " + ex.Message);
        }
        finally
        {
            element.IsEnabled = true;
        }

        Refresh();
    }

    private void ProxySelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || sender is not ComboBox { DataContext: ShortcutRow row } combo
            || combo.SelectedItem is not string selected)
        {
            return;
        }

        row.SelectedProxy = selected;
        try
        {
            _catalog.SetProxy(row.ProfileId, selected);
        }
        catch (Exception ex)
        {
            SetStatus("Proxy selection could not be saved: " + ex.Message);
        }
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

    /// <summary>
    /// Writes the selected row's API key into Claude Code's user settings.
    /// Only the three Agent Router environment values are changed; all other
    /// settings and MCP configuration remain untouched.
    /// </summary>
    private void InjectButton_Click(object sender, RoutedEventArgs e)
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

        string? temporaryPath = null;
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "settings.json");
            var directory = Path.GetDirectoryName(settingsPath)
                ?? throw new InvalidOperationException("Claude settings folder is unavailable");
            Directory.CreateDirectory(directory);

            var root = File.Exists(settingsPath)
                ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
                    ?? throw new InvalidDataException("Claude settings root is not a JSON object")
                : new JsonObject();
            var environment = root["env"] as JsonObject ?? new JsonObject();
            root["env"] = environment;
            environment["ANTHROPIC_BASE_URL"] = "https://agentrouter.org/";
            environment["ANTHROPIC_AUTH_TOKEN"] = row.ApiKey;
            environment["ANTHROPIC_MODEL"] = "deepseek-v4-flash";

            temporaryPath = settingsPath + ".tmp";
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, settingsPath, overwrite: true);
            temporaryPath = null;
            SetStatus($"{row.Account}: Claude settings injected.");
        }
        catch (Exception ex)
        {
            SetStatus("Inject failed: " + ex.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // The original settings file remains untouched if cleanup fails.
                }
            }
        }
    }

    private void SetStatus(string? message)
    {
        if (_batchRunning && !string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            ProgressTitleText.Text = _rows.Count == 0
                ? "Agent Router · ready"
                : $"{_rows.Count} profiles ready";
            StatusText.Text = "Pilih operasi untuk memulai.";
            BatchProgressBar.Value = 0;
            BatchProgressBar.Visibility = Visibility.Collapsed;
            return;
        }

        ProgressTitleText.Text = "Activity";
        StatusText.Text = message;
        BatchProgressBar.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dateTimer.Stop();
        _dateTimer.Tick -= DateTimer_Tick;
    }
}
