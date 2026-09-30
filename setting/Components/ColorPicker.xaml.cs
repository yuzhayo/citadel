using System.Windows;
using System.Windows.Media;
using Citadel.Core.Tokens;

namespace Citadel.Setting.Components;

/// <summary>Shared opaque RGB picker. Only Apply returns a colour to its owner.</summary>
public partial class SettingColorPickerDialog : SettingDialog
{
    private bool _ready;

    public SettingColorPickerDialog(string initialHex)
    {
        if (!TokenValue.TryParseColor(initialHex, out var initial) || initial.A != 0xFF)
            throw new ArgumentException("Expected an opaque #RRGGBB colour.", nameof(initialHex));

        InitializeComponent();
        RedSlider.Value = initial.R;
        GreenSlider.Value = initial.G;
        BlueSlider.Value = initial.B;
        _ready = true;
        UpdatePreview();
    }

    public string SelectedHex { get; private set; } = string.Empty;

    public static string? Pick(Window? owner, string initialHex)
    {
        var dialog = new SettingColorPickerDialog(initialHex);
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.SelectedHex : null;
    }

    private void Channel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready) UpdatePreview();
    }

    private void UpdatePreview()
    {
        var red = (byte)RedSlider.Snap(RedSlider.Value);
        var green = (byte)GreenSlider.Snap(GreenSlider.Value);
        var blue = (byte)BlueSlider.Snap(BlueSlider.Value);
        SelectedHex = $"#{red:X2}{green:X2}{blue:X2}";
        PreviewSwatch.Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
        HexReadout.Text = SelectedHex;
        RedValue.Text = red.ToString();
        GreenValue.Text = green.ToString();
        BlueValue.Text = blue.ToString();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
