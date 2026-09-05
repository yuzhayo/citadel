using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Citadel.Setting.Components;
using CitadelBridge;
using Module.Camoprof.Features.AddProfile;
using Module.Camoprof.Features.ProfileActions;
using Module.Camoprof.Network;
using Module.Camoprof.Providers.Google;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Launcher;

public partial class LauncherView : UserControl, IDisposable
{
    private const string GitHubUrl = "https://github.com/";

    private readonly ProfileCatalog _catalog;
    private readonly BrowserSessionCoordinator _sessions;
    private readonly NetworkMonitor _network;
    private readonly AddProfileFeature _addProfile;
    private readonly ProfileActionsFeature _profileActions;
    private readonly ObservableCollection<LauncherProfileRow> _profiles = [];
    private CancellationTokenSource? _addProfileCancellation;
    private bool _busy;
    private bool _disposed;

    internal LauncherView(
        ProfileCatalog catalog,
        BrowserSessionCoordinator sessions,
        NetworkMonitor network,
        AddProfileFeature addProfile,
        ProfileActionsFeature profileActions)
    {
        _catalog = catalog;
        _sessions = sessions;
        _network = network;
        _addProfile = addProfile;
        _profileActions = profileActions;
        InitializeComponent();
        ProfileTable.ItemsSource = _profiles;
        _sessions.SessionChanged += Sessions_SessionChanged;
        _network.SnapshotChanged += Network_SnapshotChanged;
        RenderNetwork(_network.Current);
    }

    internal async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var profiles = await _catalog.ScanAsync();
            if (_disposed)
            {
                return;
            }

            var previous = _profiles.ToDictionary(
                row => row.ProfileId,
                row => row.Google,
                StringComparer.OrdinalIgnoreCase);
            _profiles.Clear();
            foreach (var profile in profiles)
            {
                var row = new LauncherProfileRow(profile, _sessions.IsOpen(profile.ProfileId));
                if (previous.TryGetValue(profile.ProfileId, out var google))
                {
                    row.Google = google;
                }
                _profiles.Add(row);
            }

            EmptyText.Visibility = _profiles.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SetStatus("Profile refresh failed: " + ex.Message);
        }
    }

    private async void AddProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var profileId = "p_" + Guid.NewGuid().ToString("N");
        await RunActionAsync(async () =>
        {
            // One call, one contract: the feature owns the browser
            // session, the claim, and the credential — Launcher only
            // displays the non-secret result.
            await RunAddProfileAsync(new AddProfileRequest(profileId));
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void LaunchClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LauncherProfileRow row })
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            if (row.IsRunning)
            {
                await _sessions.CloseAsync(row.ProfileId);
                SetStatus("Browser closed for '" + row.Name + "'.");
            }
            else
            {
                await _sessions.OpenAsync(row.ProfileId);
                SetStatus("Browser opened for '" + row.Name + "'.");
            }
        });
    }

    private async void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LauncherProfileRow row })
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            if (_sessions.IsOpen(row.ProfileId))
            {
                await _sessions.NavigateAsync(row.ProfileId, GitHubUrl);
            }
            else
            {
                await _sessions.OpenAsync(row.ProfileId, GitHubUrl, headless: false);
            }
            SetStatus("GitHub opened for '" + row.Name + "'.");
        });
    }

    private async void CheckGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LauncherProfileRow row })
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var outcome = await _profileActions.CheckGoogleAsync(
                row.Profile,
                showBrowser: CheckModeToggle.IsChecked == true,
                checkStarting: () => row.Google = new GoogleAccountResult(
                    GoogleAccountState.Checking,
                    row.Profile.Email,
                    "checking network and resident profile",
                    DateTimeOffset.Now));

            if (outcome.Account is { } account)
            {
                row.Google = account;
                SetStatus(account.Label + ": " + account.Reason);
            }

            if (outcome.Enrollment is { } enrollment)
            {
                await RunAddProfileAsync(enrollment);
            }
        }, row);
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LauncherProfileRow row })
        {
            return;
        }

        bool confirmed;
        try
        {
            confirmed = SettingDialog.Confirm(
                Window.GetWindow(this),
                "CamoProf",
                "Delete profile '" + row.Name
                + "'?\n\nIts resident browser folder and account record will be removed permanently.",
                "Delete");
        }
        catch (Exception ex)
        {
            SetStatus("Delete confirmation failed: " + ex.Message);
            return;
        }
        if (!confirmed)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await _profileActions.DeleteAsync(row.ProfileId);
            await RefreshAsync();
            SetStatus("Profile '" + row.Name + "' deleted.");
        });
    }

    private async Task RunAddProfileAsync(AddProfileRequest request)
    {
        _addProfileCancellation = new CancellationTokenSource();
        AddProfileResult? result;
        try
        {
            var dialog = new AddProfileDialog(
                request,
                _addProfile,
                _addProfileCancellation.Token)
            {
                Owner = Window.GetWindow(this),
            };
            dialog.ShowDialog();
            result = dialog.Result;
        }
        finally
        {
            _addProfileCancellation.Dispose();
            _addProfileCancellation = null;
        }

        await RefreshAsync();
        SetStatus(result is null
            ? "Add Profile ended."
            : AddProfilePolicy.LauncherStatus(result));
    }

    private async Task RunActionAsync(Func<Task> action, LauncherProfileRow? row = null)
    {
        if (_busy || _disposed)
        {
            return;
        }

        _busy = true;
        ProfileTable.IsEnabled = false;
        AddProfileButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        CheckModeToggle.IsEnabled = false;
        try
        {
            await action();
        }
        catch (PyHostException ex) when (ex.Code == "BROWSER_GONE")
        {
            SetStatus("The browser window was closed; launch it again.");
        }
        catch (PyHostException ex)
        {
            SetStatus(ex.Code + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            if (row is not null)
            {
                row.Google = new GoogleAccountResult(
                    GoogleAccountState.Unknown,
                    row.Profile.Email,
                    ex.Message,
                    DateTimeOffset.Now);
            }
            SetStatus(ex.Message);
        }
        finally
        {
            if (!_disposed)
            {
                ProfileTable.IsEnabled = true;
                AddProfileButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                CheckModeToggle.IsEnabled = true;
            }
            _busy = false;
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Sessions_SessionChanged(object? sender, ProfileSessionChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Sessions_SessionChanged(sender, e));
            return;
        }
        var row = _profiles.FirstOrDefault(item => string.Equals(
            item.ProfileId,
            e.Profile,
            StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            row.IsRunning = e.IsOpen;
        }
    }

    private void Network_SnapshotChanged(object? sender, NetworkSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Network_SnapshotChanged(sender, snapshot));
            return;
        }
        RenderNetwork(snapshot);
    }

    private void RenderNetwork(NetworkSnapshot snapshot)
    {
        var checkedAt = snapshot.CheckedAt == DateTimeOffset.MinValue
            ? string.Empty
            : " · " + snapshot.CheckedAt.ToString("HH:mm:ss");
        NetworkText.Text = "Network: " + snapshot.State + checkedAt;
        NetworkText.ToolTip = snapshot.Reason;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Navigation away must not orphan a running Add Profile run:
        // the linked token cancels the dialog's run and its best-effort
        // teardown; the pyhost lifecycle hook is the final backstop.
        _addProfileCancellation?.Cancel();
        _sessions.SessionChanged -= Sessions_SessionChanged;
        _network.SnapshotChanged -= Network_SnapshotChanged;
    }
}
