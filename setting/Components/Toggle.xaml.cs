using System.Windows.Controls.Primitives;

namespace Citadel.Setting.Components;

/// <summary>An on/off switch. Two states, no tri-state.</summary>
public sealed partial class SettingToggle : ToggleButton
{
    public SettingToggle() => InitializeComponent();
}
