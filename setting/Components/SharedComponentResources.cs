using System.Windows;

namespace Citadel.Setting.Components;

internal static class SharedComponentResources
{
    public static ResourceDictionary Load(string file) => new()
    {
        Source = new Uri(
            "/Citadel.Setting;component/Components/" + file,
            UriKind.RelativeOrAbsolute),
    };
}
