using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Settings;

public partial class SettingsView : UserControl
{
    private readonly ProxySettingsStore _store;

    internal SettingsView(ProxySettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        Render(_store.Load());
        SetStatus(_store.LastLoadWarning);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new ProxySettings(
                Parse(TargetField.Text, "Target usable entries"),
                Parse(SourceTimeoutField.Text, "Source timeout"),
                Parse(TcpTimeoutField.Text, "Proxy validation timeout"),
                Parse(ParallelField.Text, "Parallel TCP checks"));
            var saved = _store.Save(settings);
            Render(saved);
            SetStatus("Settings saved.");
        }
        catch (Exception ex)
        {
            SetStatus("Settings were not saved: " + ex.Message);
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Render(_store.Reset());
            SetStatus("Defaults restored.");
        }
        catch (Exception ex)
        {
            SetStatus("Settings reset failed: " + ex.Message);
        }
    }

    private void Render(ProxySettings settings)
    {
        TargetField.Text = settings.TargetUsableEntries.ToString(CultureInfo.InvariantCulture);
        SourceTimeoutField.Text = settings.SourceRequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        TcpTimeoutField.Text = settings.ProxyValidationTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ParallelField.Text = settings.ParallelTcpChecks.ToString(CultureInfo.InvariantCulture);
    }

    private static int Parse(string value, string label) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidOperationException(label + " must be a whole number.");

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
