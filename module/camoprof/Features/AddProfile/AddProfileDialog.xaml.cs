using System.ComponentModel;
using System.Windows;
using Citadel.Setting.Components;

namespace Module.Camoprof.Features.AddProfile;

/// <summary>
/// CamoProf Add Profile content hosted by shared SettingDialog chrome
/// and composed only from shared setting controls. The dialog owns the
/// run's cancellation: it starts the feature on Loaded with a single
/// linked CancellationTokenSource; Cancel and the window Close (X) both
/// cancel and AWAIT cleanup before the window may vanish, and a
/// completion arriving after close never touches the UI.
///
/// Auto-close happens on every terminal outcome; DialogResult is true
/// only for Completed. ActiveWithoutPassword closes non-success — the
/// login worked but no password can be saved, and the Launcher status
/// line carries that honest message.
/// </summary>
public sealed partial class AddProfileDialog : SettingDialog
{
    private readonly AddProfileRequest _request;
    private readonly AddProfileFeature _feature;
    private readonly CancellationTokenSource _cts;
    private Task? _run;
    private bool _closed;
    private bool _instructionShown;

    internal AddProfileDialog(
        AddProfileRequest request,
        AddProfileFeature feature,
        CancellationToken externalCancellation)
    {
        _request = request;
        _feature = feature;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        InitializeComponent();
        Loaded += Dialog_Loaded;
        Closing += Dialog_Closing;
        Closed += Dialog_Closed;
    }

    /// <summary>Non-secret outcome; valid after the dialog has closed.</summary>
    internal AddProfileResult? Result { get; private set; }

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_run is not null)
        {
            return;
        }

        // ExecuteAsync never throws — it maps every path to a result.
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        AddProfileResult result;
        try
        {
            result = await _feature.ExecuteAsync(
                _request,
                new Progress<AddProfileUpdate>(OnUpdate),
                _cts.Token);
        }
        catch (Exception)
        {
            // The coordinator maps every failure; this is a belt-and-braces
            // guard so the dialog always reaches a terminal state.
            result = new AddProfileResult(
                AddProfileOutcome.Failed, null, "unexpected add-profile failure");
        }

        Result = result;
        if (_closed)
        {
            return;
        }

        DialogResult = result.Outcome == AddProfileOutcome.Completed;
    }

    private void OnUpdate(AddProfileUpdate update)
    {
        if (_closed)
        {
            return;
        }

        StatusText.Text = update.StatusText;
        if (!_instructionShown)
        {
            _instructionShown = true;
            InstructionText.Text = "Complete Google login in the open browser.";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private async void Dialog_Closing(object? sender, CancelEventArgs e)
    {
        if (_run is null || _run.IsCompleted)
        {
            return;
        }

        // The window may not vanish while feature cleanup is pending:
        // cancel, AWAIT the run (the coordinator does best-effort
        // teardown), then let the close complete. No callback may touch
        // a dead window.
        e.Cancel = true;
        _cts.Cancel();
        try
        {
            await _run;
        }
        catch (Exception)
        {
            // RunAsync never throws; guard anyway.
        }

        if (!_closed)
        {
            Close();
        }
    }

    private void Dialog_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _cts.Cancel();
        _cts.Dispose();
        Loaded -= Dialog_Loaded;
        Closing -= Dialog_Closing;
        Closed -= Dialog_Closed;
    }
}
