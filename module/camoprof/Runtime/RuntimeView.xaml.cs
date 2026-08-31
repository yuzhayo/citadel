using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CitadelBridge;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Runtime;

public partial class RuntimeView : UserControl
{
    private readonly BrowserSessionCoordinator _sessions;
    private bool _activated;
    private bool _busy;

    internal RuntimeView(BrowserSessionCoordinator sessions)
    {
        _sessions = sessions;
        InitializeComponent();
    }

    internal event Action<bool>? SetupBusyChanged;

    internal async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await RefreshStatusAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        if (_busy)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
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
                    ? (Brush)FindResource("Accent")
                    : (Brush)FindResource("Body");
                detail.Text = state.Detail;
            }
        }
        catch (Exception ex)
        {
            SetupStatus.Text = "status check gagal: " + ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_sessions.HasOpenSessions)
        {
            SetupStatus.Text =
                "tutup browser aktif dari tab Launcher sebelum mengubah runtime";
            return;
        }

        _busy = true;
        SetupBusyChanged?.Invoke(true);
        SetupButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        SetupProgress.Visibility = Visibility.Visible;
        SetupStatus.Text = "setup berjalan…";
        try
        {
            var completed = await Task.Run(() => RuntimeSetup.RunAsync(
                (stage, message) => Dispatcher.BeginInvoke(
                    () => SetupStatus.Text = stage + ": " + message)));
            SetupStatus.Text = completed
                ? "setup selesai"
                : "setup lain sedang berjalan — gunakan Refresh status setelah selesai";
        }
        catch (Exception ex)
        {
            SetupStatus.Text = "setup gagal: " + ex.Message;
        }
        finally
        {
            SetupProgress.Visibility = Visibility.Collapsed;
            SetupButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            _busy = false;
            SetupBusyChanged?.Invoke(false);
        }

        await RefreshStatusAsync();
    }
}
