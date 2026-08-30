using System.Windows;

namespace Citadel.Setting;

/// <summary>
/// The control library's styles. Merged by whoever hosts these screens; the
/// values themselves are DynamicResource lookups into the token bridge, so this
/// dictionary carries shape and not colour.
/// </summary>
public partial class SettingResources : ResourceDictionary
{
    public SettingResources() => InitializeComponent();
}
