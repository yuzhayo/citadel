using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>Shared internal-screen tabs and their interaction states.</summary>
public sealed class SettingTabs : TabControl
{
    public SettingTabs()
    {
        var resources = SharedComponentResources.Load("Tabs.xaml");
        Resources.MergedDictionaries.Add(resources);
        Style = (Style)resources["SettingTabsStyle"];
    }
}
