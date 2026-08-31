using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using CitadelBridge;

namespace Module.Camoprof;

/// <summary>
/// CamoProf screen: runtime status + setup, and the camoufox profile homes.
/// Listing/deleting profiles is pure C# filesystem work; Python is only
/// spawned when a browser must live (Add &amp; login / Launch / Verify).
/// The view's Lifetime owns the PyHost: navigating away shuts the host down
/// gracefully, and the host's own EOF guard is the backstop.
/// </summary>
public partial class CamoprofView : UserControl
{
    private static readonly Regex ProfileName = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ObservableCollection<ProfileRow> _profiles = new();
    private readonly Dictionary<string, string> _sessionsByProfile = new();
    private PyHost? _host;
    private bool _busy;

    public CamoprofView(Lifetime lifetime)
    {
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;

        // The lifetime outlives the visuals but not the navigation: when it
        // dies, the host must die with it. Off the UI thread — Dispose talks
        // to the child process and may wait out the grace period.
        lifetime.Add(() => Task.Run(() => Interlocked.Exchange(ref _host, null)?.Dispose()));

        Loaded += async (_, _) =>
        {
            await RefreshRuntimeAsync();
            await RefreshProfilesAsync();
        };
    }

    private sealed record ProfileRow(string Name, string LastWrite, string SizeText);

    // ---- runtime ------------------------------------------------------

    private async Task RefreshRuntimeAsync()
    {
        try
        {
            var states = await RuntimeSetup.CheckStatusAsync();
            foreach (var state in states)
            {
                var (mark, detail) = state.Name switch
                {
                    "Python" => (PythonMark, PythonDetail),
                    "venv" => (VenvMark, VenvDetail),
                    "packages" => (PackagesMark, PackagesDetail),
                    _ => (BrowserMark, BrowserDetail),
                };
                mark.Text = state.Ready ? "✓" : "✗";
                mark.Foreground = state.Ready
                    ? (System.Windows.Media.Brush)FindResource("Accent")
                    : (System.Windows.Media.Brush)FindResource("Body");
                detail.Text = state.Detail;
            }
        }
        catch (Exception ex)
        {
            SetupStatus.Text = "status check gagal: " + ex.Message;
        }
    }

    private async void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetupButton.IsEnabled = false;
        SetupProgress.Visibility = Visibility.Visible;
        SetupStatus.Text = "setup berjalan…";
        try
        {
            var completed = await Task.Run(() => RuntimeSetup.RunAsync(
                (stage, message) => Dispatcher.BeginInvoke(
                    () => SetupStatus.Text = stage + ": " + message)));
            SetupStatus.Text = completed
                ? "setup selesai"
                : "setup lain sedang berjalan — status akan diperbarui saat Refresh";
        }
        catch (Exception ex)
        {
            SetupStatus.Text = "setup gagal: " + ex.Message;
        }
        finally
        {
            SetupProgress.Visibility = Visibility.Collapsed;
            SetupButton.IsEnabled = true;
            _busy = false;
        }

        await RefreshRuntimeAsync();
    }

    // ---- profiles -----------------------------------------------------

    private async Task RefreshProfilesAsync()
    {
        var root = CredenzPath.ProfilesRoot();
        var rows = await Task.Run(() => ScanProfiles(root));
        _profiles.Clear();
        foreach (var row in rows)
        {
            _profiles.Add(row);
        }
    }

    private static List<ProfileRow> ScanProfiles(string root)
    {
        var rows = new List<ProfileRow>();
        if (!Directory.Exists(root))
        {
            return rows;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            var info = new DirectoryInfo(directory);
            rows.Add(new ProfileRow(
                info.Name,
                info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                FormatSize(DirectorySize(directory))));
        }

        return rows;
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception)
        {
            return -1; // a live browser locks files; size is cosmetic
        }
    }

    private static string FormatSize(long bytes)
        => bytes < 0 ? "—"
            : bytes < 1L << 20 ? (bytes / 1024.0).ToString("0.#") + " KB"
            : bytes < 1L << 30 ? (bytes / 1048576.0).ToString("0.#") + " MB"
            : (bytes / 1073741824.0).ToString("0.##") + " GB";

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = ProfileList.SelectedItem is not null;
        LaunchButton.IsEnabled = hasSelection && !_busy;
        VerifyButton.IsEnabled = hasSelection && !_busy;
        DeleteButton.IsEnabled = hasSelection && !_busy;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshProfilesAsync();

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewProfileName.Text.Trim();
        if (!ProfileName.IsMatch(name) || name is "." or "..")
        {
            StatusText.Text = "nama tidak sah — huruf/angka/titik/minus/underscore saja";
            return;
        }

        if (Directory.Exists(Path.Combine(CredenzPath.ProfilesRoot(), name)))
        {
            StatusText.Text = "profile '" + name + "' sudah ada — pilih dari daftar untuk Launch";
            return;
        }

        await RunBrowserActionAsync(async host =>
        {
            var response = await host.OpenSessionAsync(name, null);
            TrackSession(name, response);
            // Profile baru langsung masuk daftar dan terpilih, supaya tombol
            // Verify tersedia tanpa Refresh manual (codex audit #5).
            await RefreshProfilesAsync();
            var selected = _profiles.FirstOrDefault(p => p.Name == name);
            if (selected is not null)
            {
                ProfileList.SelectedItem = selected;
            }

            NewProfileName.Text = string.Empty;
            StatusText.Text =
                "browser terbuka untuk '" + name + "' — login di jendelanya, lalu Verify login";
        });
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
        {
            return;
        }

        await RunBrowserActionAsync(async host =>
        {
            var response = await host.OpenSessionAsync(row.Name, null);
            TrackSession(row.Name, response);
            StatusText.Text = "session terbuka untuk '" + row.Name + "'";
        });
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
        {
            return;
        }

        if (!_sessionsByProfile.TryGetValue(row.Name, out var sessionId))
        {
            StatusText.Text = "tidak ada session terbuka untuk '" + row.Name + "' — Launch dulu";
            return;
        }

        await RunBrowserActionAsync(async host =>
        {
            try
            {
                var response = await host.VerifySessionAsync(sessionId);
                var alive = response["alive"]?.GetValue<bool>() == true;
                StatusText.Text = alive
                    ? "✓ session HIDUP untuk '" + row.Name + "'"
                        + (response["state_saved"]?.GetValue<bool>() == true
                            ? " — kunci sesi tercetak"
                            : string.Empty)
                    : "✗ belum masuk — lanjutkan login di jendela browser, lalu Verify lagi";
            }
            catch (PyHostException ex) when (ex.Code == "BROWSER_GONE")
            {
                // pyhost dropped the dead session server-side; resync here.
                _sessionsByProfile.Remove(row.Name);
                StatusText.Text = "jendela browser sudah tertutup — Launch ulang";
            }
        });
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            Window.GetWindow(this),
            "Hapus profile '" + row.Name + "'?\n\nFolder browser-nya dihapus permanen dari credenz.",
            "CamoProf",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBrowserActionAsync(async host =>
        {
            // 1 — a live session on this profile closes first.
            if (_sessionsByProfile.TryGetValue(row.Name, out var sessionId))
            {
                try
                {
                    await host.CloseSessionAsync(sessionId);
                    _sessionsByProfile.Remove(row.Name);
                }
                catch (PyHostException ex) when (
                    ex.Code is "SESSION_NOT_FOUND" or "BROWSER_GONE")
                {
                    // These two outcomes prove there is no live registered
                    // session. TIMEOUT / close failure must abort deletion:
                    // recursive delete could otherwise partially damage a
                    // profile that its browser still owns.
                    _sessionsByProfile.Remove(row.Name);
                }
            }

            // 2 — resolve, then prove the target stays inside the root.
            var root = Path.GetFullPath(CredenzPath.ProfilesRoot());
            var target = Path.GetFullPath(Path.Combine(root, row.Name));
            if (!target.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "penghapusan ditolak: path keluar dari root profile";
                return;
            }

            if (Directory.Exists(target))
            {
                await Task.Run(() => Directory.Delete(target, recursive: true));
            }

            StatusText.Text = "profile '" + row.Name + "' dihapus";
        });

        await RefreshProfilesAsync();
    }

    // ---- host plumbing --------------------------------------------------

    private void TrackSession(string profile, System.Text.Json.Nodes.JsonObject response)
    {
        var sessionId = response["session"]?.GetValue<string>();
        if (sessionId is not null)
        {
            _sessionsByProfile[profile] = sessionId;
        }
    }

    private PyHost EnsureHost()
    {
        if (_host is not null)
        {
            return _host;
        }

        var python = RuntimeSetup.VenvPython;
        if (!File.Exists(python))
        {
            throw new InvalidOperationException(
                "runtime belum siap — jalankan Setup runtime dulu");
        }

        var script = RuntimeSetup.DeployedPyhostScript;
        if (!File.Exists(script))
        {
            throw new InvalidOperationException(
                "payload pyhost tidak ter-deploy: " + script);
        }

        _host = PyHost.Start(python, script, CredenzPath.Resolve());
        return _host;
    }

    private async Task RunBrowserActionAsync(Func<PyHost, Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetActionButtonsEnabled(false);
        try
        {
            await action(EnsureHost());
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
            _busy = false;
            SetActionButtonsEnabled(ProfileList.SelectedItem is not null);
        }
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        AddButton.IsEnabled = !_busy;
        LaunchButton.IsEnabled = enabled;
        VerifyButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
    }
}
