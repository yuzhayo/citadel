using System.Windows;
using Citadel.Setting.Components;
using Module.Camoprof.Providers.Google;

namespace Module.Camoprof.Launcher;

/// <summary>
/// CamoProf-specific account pairing content hosted by shared SettingDialog
/// chrome and composed only from shared setting controls.
/// </summary>
public sealed partial class AccountSetupDialog : SettingDialog
{
    private readonly string _profileId;
    private readonly GoogleAccountService _google;
    private readonly GoogleCredentialStore _credentials;
    private string? _email;
    private bool _busy;
    private bool _closed;

    internal AccountSetupDialog(
        string profileId,
        string? existingEmail,
        GoogleAccountService google,
        GoogleCredentialStore credentials)
    {
        _profileId = profileId;
        _google = google;
        _credentials = credentials;
        InitializeComponent();
        Closed += AccountSetupDialog_Closed;

        if (string.IsNullOrWhiteSpace(existingEmail))
        {
            InstructionText.Text =
                "Complete Google login in the open browser, then detect the active account.";
            return;
        }

        SetDetectedEmail(existingEmail);
        InstructionText.Text = "Enter the replacement password for " + existingEmail + ".";
        DetectButton.Visibility = Visibility.Collapsed;
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _google.DetectAsync(_profileId);
            if (_closed)
            {
                return;
            }
            if (result.State == GoogleAccountState.Active
                && !string.IsNullOrWhiteSpace(result.Email))
            {
                SetDetectedEmail(result.Email);
                InstructionText.Text = "Account detected. Enter its password for future relog.";
                StatusText.Text = string.Empty;
            }
            else
            {
                StatusText.Text = result.Label + ": " + result.Reason;
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                StatusText.Text = ex.Message;
            }
        }
        finally
        {
            if (!_closed)
            {
                SetBusy(false);
            }
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_email))
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _credentials.SaveAsync(_profileId, _email, PasswordField.Password);
            if (_closed)
            {
                return;
            }
            PasswordField.Clear();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                StatusText.Text = ex.Message;
                SetBusy(false);
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        PasswordField.Clear();
        DialogResult = false;
    }

    private void SetDetectedEmail(string email)
    {
        _email = email;
        AccountText.Text = email;
        PasswordPanel.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DetectButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_email);
        CancelButton.IsEnabled = !busy;
        PasswordField.IsEnabled = !busy;
    }

    private void AccountSetupDialog_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        PasswordField.Clear();
        Closed -= AccountSetupDialog_Closed;
    }
}
