using System.ComponentModel;
using System.Windows;
using Citadel.Setting.Components;

namespace Module.Camoprof.Providers.Google.Enrollment;

/// <summary>
/// CamoProf enrollment content hosted by shared SettingDialog chrome and
/// composed only from shared setting controls. The dialog owns the
/// enrollment cancellation: it starts the run on Loaded with a single
/// linked CancellationTokenSource; Cancel and the window Close (X) both
/// cancel and AWAIT cleanup before the window may vanish, and a
/// completion arriving after close never touches the UI.
///
/// Auto-close happens on every terminal outcome; DialogResult is true
/// only for Completed. ActiveWithoutPassword closes non-success — the
/// login worked but no password can be saved, and the Launcher status
/// line carries that honest message.
/// </summary>
public sealed partial class GoogleEnrollmentDialog : SettingDialog
{
    private readonly string _profileId;
    private readonly string? _expectedEmail;
    private readonly GoogleEnrollmentFeature _feature;
    private readonly CancellationTokenSource _cts;
    private Task? _run;
    private bool _closed;
    private bool _instructionShown;

    internal GoogleEnrollmentDialog(
        string profileId,
        string? expectedEmail,
        GoogleEnrollmentFeature feature,
        CancellationToken externalCancellation)
    {
        _profileId = profileId;
        _expectedEmail = expectedEmail;
        _feature = feature;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        InitializeComponent();
        Loaded += Dialog_Loaded;
        Closing += Dialog_Closing;
        Closed += Dialog_Closed;
    }

    /// <summary>Non-secret outcome; valid after the dialog has closed.</summary>
    internal GoogleEnrollmentResult? Result { get; private set; }

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_run is not null)
        {
            return;
        }

        // RunAsync never throws — it maps every path to a result.
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        GoogleEnrollmentResult result;
        try
        {
            result = await _feature.EnrollAsync(
                _profileId,
                _expectedEmail,
                new Progress<GoogleEnrollmentUpdate>(OnUpdate),
                _cts.Token);
        }
        catch (Exception)
        {
            // The service maps every failure; this is a belt-and-braces
            // guard so the dialog always reaches a terminal state.
            result = new GoogleEnrollmentResult(
                GoogleEnrollmentOutcome.Failed, null, "unexpected enrollment failure");
        }

        Result = result;
        if (_closed)
        {
            return;
        }

        DialogResult = result.Outcome == GoogleEnrollmentOutcome.Completed;
    }

    private void OnUpdate(GoogleEnrollmentUpdate update)
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

        // The window may not vanish while enrollment cleanup is pending:
        // cancel, AWAIT the run (the service does best-effort teardown),
        // then let the close complete. No callback may touch a dead window.
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
