using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CitadelBridge;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Launcher;

public partial class LauncherView : UserControl, IDisposable
{
    private readonly ProfileCatalog _catalog;
    private readonly BrowserSessionCoordinator _sessions;
    private readonly ObservableCollection<LauncherProfileRow> _profiles = new();
    private bool _busy;
    private bool _disposed;

    internal LauncherView(ProfileCatalog catalog, BrowserSessionCoordinator sessions)
    {
        _catalog = catalog;
        _sessions = sessions;
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
        _sessions.SessionChanged += Sessions_SessionChanged;
    }

    internal async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var profiles = await _catalog.ScanAsync(includeSize: false);
            if (_disposed)
            {
                return;
            }

            _profiles.Clear();
            foreach (var profile in profiles)
            {
                _profiles.Add(new LauncherProfileRow(profile, _sessions.IsOpen(profile.Name)));
            }

            EmptyText.Visibility = _profiles.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "profile refresh gagal: " + ex.Message;
        }
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
                await _sessions.CloseAsync(row.Name);
                StatusText.Text = "browser ditutup untuk '" + row.Name + "'";
            }
            else
            {
                await _sessions.OpenAsync(row.Name);
                StatusText.Text = "browser terbuka untuk '" + row.Name + "'";
            }
        });
    }

    private async void VerifyGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LauncherProfileRow row })
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var response = await _sessions.VerifyGoogleAsync(row.Name);
            var alive = response["alive"]?.GetValue<bool>() == true;
            StatusText.Text = alive
                ? "✓ Google session aktif untuk '" + row.Name + "'"
                : "○ Google belum login untuk '" + row.Name + "'";
        });
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_busy || _disposed)
        {
            return;
        }

        _busy = true;
        ProfileList.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (PyHostException ex) when (ex.Code == "BROWSER_GONE")
        {
            StatusText.Text = "jendela browser sudah tertutup — Launch ulang";
        }
        catch (PyHostException ex)
        {
            StatusText.Text = ex.Code + ": " + ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                ProfileList.IsEnabled = true;
                RefreshButton.IsEnabled = true;
            }

            _busy = false;
        }
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

        var row = _profiles.FirstOrDefault(
            item => string.Equals(item.Name, e.Profile, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            row.IsRunning = e.IsOpen;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.SessionChanged -= Sessions_SessionChanged;
    }
}
