using System.Windows;
using System.Windows.Controls;

namespace Citadel.Ui.Controls;

public sealed class RailButton : Button
{
    static RailButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(RailButton),
            new FrameworkPropertyMetadata(typeof(RailButton)));
    }
}
