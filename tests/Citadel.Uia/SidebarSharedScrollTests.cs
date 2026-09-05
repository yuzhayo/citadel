using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Setting;
using Citadel.Ui.Controls;
using Citadel.Ui.Theme;

namespace Citadel.Uia;

/// <summary>
/// The Sidebar list template asks for the shared ScrollViewer style through a
/// deferred resource reference, because Citadel.Ui does not reference
/// Citadel.Setting and the style is only reachable once App.xaml has merged
/// both dictionaries. An unresolved reference sets nothing and quietly leaves
/// the sidebar on default system chrome, so these tests compose the
/// dictionaries exactly as App.xaml does and assert what actually resolves.
/// </summary>
public sealed class SidebarSharedScrollTests
{
    [Fact]
    public void SidebarList_OverflowingContent_RendersThroughTheSharedScrollViewerStyle()
    {
        Sta.Run(() =>
        {
            var lifetime = new Lifetime();
            try
            {
                var root = WithApplicationDictionaries(new Border { Width = 240, Height = 160 }, lifetime);
                var list = new ListBox
                {
                    ItemsSource = Enumerable
                        .Range(0, 40)
                        .Select(_ => new Border { Height = 30 })
                        .ToArray(),
                };
                list.SetResourceReference(FrameworkElement.StyleProperty, "SidebarListStyle");
                root.Child = list;
                Arrange(root);

                var scrollViewer = Descendants<ScrollViewer>(list).Single();
                var shared = Assert.IsType<Style>(root.FindResource("SettingScrollViewerStyle"));

                Assert.Same(shared, scrollViewer.Style);

                var vertical = Descendants<ScrollBar>(scrollViewer)
                    .Single(bar => bar.Name == "PART_VerticalScrollBar");
                Assert.Equal("VerticalScrollBar", AutomationProperties.GetAutomationId(vertical));
                Assert.Equal(10, vertical.ActualWidth);
                Assert.Equal(Visibility.Visible, vertical.Visibility);
            }
            finally
            {
                lifetime.Destroy();
            }
        });
    }

    [Fact]
    public void UnstyledScrollViewer_IsNotCapturedByAnImplicitGlobalStyle()
    {
        Sta.Run(() =>
        {
            var lifetime = new Lifetime();
            try
            {
                var root = WithApplicationDictionaries(new Border { Width = 240, Height = 160 }, lifetime);
                var scrollViewer = new ScrollViewer { Content = new Border { Height = 800 } };
                root.Child = scrollViewer;
                Arrange(root);

                // Wiring the Sidebar must not restyle every other ScrollViewer
                // in the application through an implicit global style.
                Assert.Null(scrollViewer.Style);
            }
            finally
            {
                lifetime.Destroy();
            }
        });
    }

    /// <summary>Mirrors the merged dictionary composition of App.xaml.</summary>
    private static T WithApplicationDictionaries<T>(T element, Lifetime lifetime)
        where T : FrameworkElement
    {
        var theme = new ThemeResources();
        theme.Bind(new Tokens(), lifetime);
        element.Resources.MergedDictionaries.Add(theme);
        element.Resources.MergedDictionaries.Add(new RailButtonResources());
        element.Resources.MergedDictionaries.Add(new SettingResources());
        return element;
    }

    private static void Arrange(FrameworkElement element)
    {
        var width = double.IsNaN(element.Width) ? 240 : element.Width;
        var height = double.IsNaN(element.Height) ? 160 : element.Height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.ApplyTemplate();
        element.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
