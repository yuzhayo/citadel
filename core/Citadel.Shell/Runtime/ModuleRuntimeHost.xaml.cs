using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Setting.Components;

namespace Citadel.Shell;

/// <summary>
/// Stable per-route presentation shell. The sidebar and Router keep this host;
/// the citizen runtime view mounts inside it only while Running.
///
/// Two lifetimes: the host survives navigation (IRetainedViewModule); the
/// runtime view unmounts on detach and dies only on safe Stop / Force Stop /
/// actual unregister. A stopped or faulted host never calls CreateView merely
/// because it was opened.
///
/// Header: while Running, composes the runtime view's own header action (fresh
/// each refresh) followed by Stop. While stopped/faulted, returns no action —
/// Start/Retry stays on the host screen.
/// </summary>
public sealed partial class ModuleRuntimeHost : System.Windows.Controls.UserControl, IRetainedViewModule, IContentHeaderActionProvider
{
    private readonly ModuleRuntimeCoordinator _coordinator;
    private readonly string _route;
    private bool _operationInFlight;
    private bool _disposed;

    public ModuleRuntimeHost(ModuleRuntimeCoordinator coordinator, string route)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _route = route ?? throw new ArgumentNullException(nameof(route));
        InitializeComponent();

        BadgeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        StatusCard.ActionContent = BadgeText;

        _coordinator.RuntimePresentationChanged += OnRuntimePresentationChanged;
        Unloaded += OnUnloaded;
        RefreshSurface();
    }

    /// <summary>Test seam: where a running citizen's view mounts.</summary>
    internal ContentControl RuntimeSurfaceElement => RuntimeSurface;

    /// <summary>Test seam: the status card shown when not Running.</summary>
    internal FrameworkElement StatusPanelElement => StatusPanel;

    internal System.Windows.Controls.Button PrimaryButtonElement => PrimaryButton;

    internal System.Windows.Controls.Button ForceStopButtonElement => ForceStopButton;

    internal System.Windows.Controls.Button RetryRemovalButtonElement => RetryRemovalButton;

    internal TextBlock TitleTextElement => TitleText;

    internal TextBlock StateTextElement => StateText;

    internal TextBlock DetailTextElement => DetailText;

    internal TextBlock BadgeText { get; private set; } = new();

    internal string Route => _route;

    private ModuleDescriptor? Descriptor =>
        _coordinator.DescriptorOf(_route);

    private string ModuleTitle => Descriptor?.Title ?? _route;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Router drives detach via IRetainedViewModule; Unloaded is only a
        // safety net when the host leaves the tree without that callback.
        if (_disposed) return;
        if (_coordinator.StateOf(_route) == ModuleRuntimeState.Running)
            _coordinator.UnmountRuntimeView(_route);
    }

    private void OnRuntimePresentationChanged(string route)
    {
        if (!string.Equals(route, _route, StringComparison.Ordinal)) return;
        if (Dispatcher.CheckAccess()) RefreshSurface();
        else Dispatcher.BeginInvoke(RefreshSurface);
    }

    /// <summary>UI-thread only. Rebuilds the status/runtime split from slot state.</summary>
    internal void RefreshSurface()
    {
        if (_disposed) return;

        var state = _coordinator.StateOf(_route);
        var slot = _coordinator.SlotOrNull(_route);
        var title = ModuleTitle;

        TitleText.Text = title;
        AutomationProperties.SetName(this, title);

        // RemovalPending (Gate 6): retained host shows release failure + Retry
        // removal even if a runtime is still Running — sidebar item stays until
        // a successful retry (or a later successful Unregister).
        var removalPending = _coordinator.IsRemovalPending(_route);
        if (removalPending)
        {
            StatusPanel.Visibility = Visibility.Visible;
            RuntimeSurface.Visibility = Visibility.Collapsed;
            if (RuntimeSurface.Content is not null && !ReferenceEquals(RuntimeSurface.Content, slot?.RuntimeView))
            {
                RuntimeSurface.Content = null;
            }
            ApplyRemovalPendingChrome(slot, title);
            return;
        }

        if (state == ModuleRuntimeState.Running && slot?.RuntimeView is not null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            RuntimeSurface.Visibility = Visibility.Visible;
            _coordinator.MountRuntimeView(_route, RuntimeSurface);
            ApplyRunningChrome(title);
            return;
        }

        StatusPanel.Visibility = Visibility.Visible;
        RuntimeSurface.Visibility = Visibility.Collapsed;
        // Unmount already cleared a mounted view; never leave a stale child here.
        if (RuntimeSurface.Content is not null && !ReferenceEquals(RuntimeSurface.Content, slot?.RuntimeView))
        {
            RuntimeSurface.Content = null;
        }
        else if (slot?.RuntimeView is null && RuntimeSurface.Content is not null)
        {
            RuntimeSurface.Content = null;
        }

        ApplyStatusChrome(state, slot, title);
    }

    private void ApplyRemovalPendingChrome(ModuleRuntimeSlot? slot, string title)
    {
        var removalError = _coordinator.RemovalError(_route)
            ?? "Runtime release failed; the source folder may already be gone.";
        var runtimeState = slot?.SnapshotState() ?? ModuleRuntimeState.Stopped;

        StateText.Text = "Removal pending";
        DetailText.Text = removalError;
        BadgeText.Text = "Removal pending";
        ForceStopButton.Visibility = Visibility.Collapsed;

        // Still expose Stop/Force Stop if a live runtime remains after a failed
        // release — Retry removal alone cannot interrupt a stuck citizen stop.
        if (runtimeState == ModuleRuntimeState.Running)
        {
            PrimaryButton.Content = $"Stop {title}";
            AutomationProperties.SetName(PrimaryButton, $"Stop {title}");
            PrimaryButton.IsEnabled = !_operationInFlight;
            PrimaryButton.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(slot?.LastError))
            {
                ShowForceStop(title, enabled: !_operationInFlight);
            }
        }
        else
        {
            PrimaryButton.Content = "Removal pending";
            AutomationProperties.SetName(PrimaryButton, $"Removal pending {title}");
            PrimaryButton.IsEnabled = false;
            PrimaryButton.Visibility = Visibility.Visible;
        }

        RetryRemovalButton.Content = $"Retry removal {title}";
        AutomationProperties.SetAutomationId(RetryRemovalButton, $"Retry removal {title}");
        AutomationProperties.SetName(RetryRemovalButton, $"Retry removal {title}");
        RetryRemovalButton.IsEnabled = !_operationInFlight;
        RetryRemovalButton.Visibility = Visibility.Visible;
    }

    private void ApplyRunningChrome(string title)
    {
        StateText.Text = "Runtime running";
        DetailText.Text = "Navigation only detaches this screen; the runtime stays up until Stop.";
        BadgeText.Text = "Running";
        PrimaryButton.Content = $"Stop {title}";
        AutomationProperties.SetAutomationId(PrimaryButton, $"Stop {title}");
        AutomationProperties.SetName(PrimaryButton, $"Stop {title}");
        PrimaryButton.IsEnabled = !_operationInFlight;
        PrimaryButton.Visibility = Visibility.Visible;
        ForceStopButton.Visibility = Visibility.Collapsed;
        RetryRemovalButton.Visibility = Visibility.Collapsed;
    }

    private void ApplyStatusChrome(ModuleRuntimeState state, ModuleRuntimeSlot? slot, string title)
    {
        var error = slot?.LastError;
        ForceStopButton.Visibility = Visibility.Collapsed;
        RetryRemovalButton.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = !_operationInFlight;

        switch (state)
        {
            case ModuleRuntimeState.Starting:
                StateText.Text = "Starting…";
                DetailText.Text = "The module runtime is starting.";
                BadgeText.Text = "Starting";
                PrimaryButton.Content = "Starting…";
                AutomationProperties.SetName(PrimaryButton, $"Starting {title}");
                PrimaryButton.IsEnabled = false;
                break;

            case ModuleRuntimeState.Stopping:
                StateText.Text = "Stopping…";
                DetailText.Text = "Waiting for the module runtime to stop safely.";
                BadgeText.Text = "Stopping";
                PrimaryButton.Content = "Stopping…";
                AutomationProperties.SetName(PrimaryButton, $"Stopping {title}");
                PrimaryButton.IsEnabled = false;
                ShowForceStop(title, enabled: !_operationInFlight);
                break;

            case ModuleRuntimeState.ForceStopping:
                StateText.Text = "Force stopping…";
                DetailText.Text = "Aborting the module runtime without waiting for drain.";
                BadgeText.Text = "Force stopping";
                PrimaryButton.Content = "Force stopping…";
                AutomationProperties.SetName(PrimaryButton, $"Force stopping {title}");
                PrimaryButton.IsEnabled = false;
                break;

            case ModuleRuntimeState.Faulted:
                StateText.Text = "Start failed";
                DetailText.Text = error ?? "The module runtime failed to start.";
                BadgeText.Text = "Faulted";
                PrimaryButton.Content = $"Retry start {title}";
                AutomationProperties.SetName(PrimaryButton, $"Retry start {title}");
                break;

            case ModuleRuntimeState.Running when error is not null:
                // Failed Stop: resources retained, retry Stop or Force Stop.
                StateText.Text = "Stop failed";
                DetailText.Text = error;
                BadgeText.Text = "Stop failed";
                PrimaryButton.Content = $"Stop {title}";
                AutomationProperties.SetName(PrimaryButton, $"Stop {title}");
                ShowForceStop(title, enabled: !_operationInFlight);
                break;

            default:
                StateText.Text = "Runtime stopped";
                DetailText.Text = "No module runtime is running. Start opens this module's normal view.";
                BadgeText.Text = "Stopped";
                PrimaryButton.Content = $"Start {title}";
                AutomationProperties.SetName(PrimaryButton, $"Start {title}");
                break;
        }
    }

    private void ShowForceStop(string title, bool enabled)
    {
        ForceStopButton.Content = $"Force stop {title}";
        AutomationProperties.SetName(ForceStopButton, $"Force stop {title}");
        ForceStopButton.IsEnabled = enabled;
        ForceStopButton.Visibility = Visibility.Visible;
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_operationInFlight) return;
        var state = _coordinator.StateOf(_route);
        _operationInFlight = true;
        RefreshSurface();
        try
        {
            switch (state)
            {
                case ModuleRuntimeState.Stopped:
                    await _coordinator.StartAsync(_route);
                    break;
                case ModuleRuntimeState.Faulted:
                    await _coordinator.StartAsync(_route);
                    break;
                case ModuleRuntimeState.Running:
                    await _coordinator.StopAsync(_route);
                    break;
                default:
                    break;
            }
        }
        catch (Exception exception)
        {
            Citadel.Core.Log.Main(
                $"[RuntimeHost] '{_route}' primary action failed: {exception.GetBaseException().Message}");
        }
        finally
        {
            _operationInFlight = false;
            RefreshSurface();
        }
    }

    private async void OnForceStopClick(object sender, RoutedEventArgs e)
    {
        if (_operationInFlight) return;
        var title = ModuleTitle;
        var owner = Window.GetWindow(this);
        var confirmed = SettingDialog.Confirm(
            owner,
            $"Force stop {title}?",
            $"Active work in {title} may be interrupted and require manual Resume/recovery afterward. "
            + "Normal Stop is skipped; unfinished durable work stays paused for explicit recovery.",
            "Force stop");
        if (!confirmed) return;

        _operationInFlight = true;
        RefreshSurface();
        try
        {
            await _coordinator.ForceStopAsync(_route);
        }
        catch (Exception exception)
        {
            Citadel.Core.Log.Main(
                $"[RuntimeHost] '{_route}' force stop failed: {exception.GetBaseException().Message}");
        }
        finally
        {
            _operationInFlight = false;
            RefreshSurface();
        }
    }

    private async void OnRetryRemovalClick(object sender, RoutedEventArgs e)
    {
        if (_operationInFlight) return;
        _operationInFlight = true;
        RefreshSurface();
        try
        {
            // Re-runs ReleaseRouteAsync on the in-memory descriptor; does not
            // Start and does not need the source folder to exist. Completion
            // comes back via RemovalStateChanged → RuntimePresentationChanged.
            await _coordinator.RetryRemovalAsync(_route);
        }
        catch (Exception exception)
        {
            Citadel.Core.Log.Main(
                $"[RuntimeHost] '{_route}' retry removal failed: {exception.GetBaseException().Message}");
        }
        finally
        {
            _operationInFlight = false;
            RefreshSurface();
        }
    }

    public void OnViewDetached()
    {
        if (_disposed) return;
        _coordinator.UnmountRuntimeView(_route);
    }

    public void OnViewAttached()
    {
        if (_disposed) return;
        RefreshSurface();
    }

    /// <summary>
    /// Running: fresh inner action (never cached) + Stop. Otherwise: no header
    /// action — Start/Retry remains on the host screen.
    /// </summary>
    public FrameworkElement CreateContentHeaderAction()
    {
        if (_coordinator.IsRemovalPending(_route)) return null!;
        if (_coordinator.StateOf(_route) != ModuleRuntimeState.Running) return null!;
        var slot = _coordinator.SlotOrNull(_route);
        if (slot?.RuntimeView is null) return null!;

        var title = ModuleTitle;
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (slot.RuntimeView is IContentHeaderActionProvider provider)
        {
            try
            {
                var inner = provider.CreateContentHeaderAction();
                if (inner is not null) row.Children.Add(inner);
            }
            catch (Exception exception)
            {
                Citadel.Core.Log.Main(
                    $"[RuntimeHost] inner header action skipped: {exception.Message}");
            }
        }

        if (slot.SnapshotState() == ModuleRuntimeState.Running
            && string.IsNullOrEmpty(slot.LastError))
        {
            var stop = new SettingButton
            {
                Content = $"Stop {title}",
                MinWidth = 88,
                Margin = new Thickness(8, 0, 0, 0),
            };
            AutomationProperties.SetName(stop, $"Stop {title}");
            stop.Click += async (_, _) =>
            {
                try
                {
                    await _coordinator.StopAsync(_route);
                }
                catch (Exception exception)
                {
                    Citadel.Core.Log.Main(
                        $"[RuntimeHost] header stop failed: {exception.GetBaseException().Message}");
                }
            };
            row.Children.Add(stop);
        }

        return row;
    }

    /// <summary>Router/host disposal: unsubscribe and drop surface ownership.</summary>
    internal void DisposeHost()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.RuntimePresentationChanged -= OnRuntimePresentationChanged;
        Unloaded -= OnUnloaded;
        _coordinator.UnmountRuntimeView(_route);
        RuntimeSurface.Content = null;
    }
}
