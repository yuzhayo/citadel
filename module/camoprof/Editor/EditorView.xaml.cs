using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CitadelBridge;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Editor;

public partial class EditorView : UserControl
{
    private readonly ProfileCatalog _catalog;
    private readonly BrowserSessionCoordinator _sessions;
    private readonly ObservableCollection<ProfileEntry> _profiles = new();
    private bool _activated;
    private bool _busy;

    internal EditorView(ProfileCatalog catalog, BrowserSessionCoordinator sessions)
    {
        _catalog = catalog;
        _sessions = sessions;
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
    }

    internal event EventHandler? ProfilesChanged;

    internal async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var profiles = await _catalog.ScanAsync(includeSize: true);
            _profiles.Clear();
            foreach (var profile in profiles)
            {
                _profiles.Add(profile);
            }

            ProfileArea.Visibility = _profiles.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch (Exception ex)
        {
            SetStatus("profile refresh gagal: " + ex.Message);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewProfileName.Text.Trim();
        if (!_catalog.IsValidName(name))
        {
            SetStatus("nama tidak sah — huruf/angka/titik/minus/underscore saja");
            return;
        }

        if (_catalog.Exists(name))
        {
            SetStatus("profile '" + name + "' sudah ada");
            return;
        }

        await RunActionAsync(async () =>
        {
            await _sessions.OpenAsync(name);
            await RefreshAsync();
            NewProfileName.Text = string.Empty;
            SetStatus("browser terbuka untuk '" + name
                + "' — login, lalu Verify Google dari Launcher");
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileEntry profile })
        {
            return;
        }

        var confirmed = MessageBox.Show(
            Window.GetWindow(this),
            "Hapus profile '" + profile.Name + "'?\n\nFolder browser-nya dihapus permanen dari credenz.",
            "CamoProf",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            // Close must succeed or prove absence before filesystem deletion.
            await _sessions.CloseAsync(profile.Name);
            await _catalog.DeleteAsync(profile.Name);
            await RefreshAsync();
            SetStatus("profile '" + profile.Name + "' dihapus");
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        AddButton.IsEnabled = false;
        ProfileList.IsEnabled = false;
        try
        {
            await action();
        }
        catch (PyHostException ex)
        {
            SetStatus(ex.Code + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            AddButton.IsEnabled = true;
            ProfileList.IsEnabled = true;
            _busy = false;
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
