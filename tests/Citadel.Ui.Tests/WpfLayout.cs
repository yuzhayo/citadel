using System.Windows;
using System.Windows.Media;

namespace Citadel.Ui.Tests;

internal static class WpfLayout
{
    public static void Arrange(FrameworkElement element, double width, double height = 360)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.ApplyTemplate();
        element.UpdateLayout();

        // Templates and generated item containers can invalidate measure once
        // after first application. A second pass settles the actual geometry.
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    public static Point CenterIn(FrameworkElement element, Visual ancestor)
    {
        var transform = element.TransformToAncestor(ancestor);
        return transform.Transform(new Point(element.ActualWidth / 2d, element.ActualHeight / 2d));
    }
}
