using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Citadel.Core.Tokens;
using Citadel.Setting;
using Citadel.Setting.Components;
using Citadel.Shell;

namespace Citadel.Uia;

[Collection("Shell power saving serial")]
public class FluidLayoutTests
{
    [Fact]
    public void ContainedViewport_GivesItsChildFiniteStretchedBoundsWithoutAScroller()
    {
        Sta.Run(() =>
        {
            var child = new Border();
            var viewport = WithResources(new SettingViewport
            {
                Mode = SettingViewportMode.Contained,
                Width = 640,
                Height = 300,
                Content = child,
            });

            Arrange(viewport);

            Assert.Empty(Descendants<ScrollViewer>(viewport));
            Assert.InRange(child.ActualWidth, 629, 631);
            Assert.InRange(child.ActualHeight, 289, 291);
        });
    }

    [Fact]
    public void DocumentViewport_FillsWidthAndUsesOnlyVerticalFallbackOverflow()
    {
        Sta.Run(() =>
        {
            var text = new TextBlock
            {
                Height = 600,
                Text = new string('x', 4_000),
                TextWrapping = TextWrapping.Wrap,
            };
            var viewport = WithResources(new SettingViewport
            {
                Mode = SettingViewportMode.Document,
                Width = 640,
                Height = 300,
                Content = text,
            });

            Arrange(viewport);
            var scroller = Assert.Single(Descendants<ScrollViewer>(viewport));

            Assert.True(scroller.ScrollableHeight > 0);
            Assert.Equal(0, scroller.ScrollableWidth);
            Assert.InRange(
                Math.Abs(text.ActualWidth - scroller.ViewportWidth),
                0,
                0.5);
        });
    }

    [Fact]
    public void Tabs_StretchSelectedViewportAndCardAcrossTheContentBand()
    {
        Sta.Run(() =>
        {
            var card = new SettingActionCard { Content = new TextBlock { Text = "content" } };
            var viewport = new SettingViewport
            {
                Mode = SettingViewportMode.Contained,
                Content = card,
            };
            var tabs = WithResources(new SettingTabs
            {
                Width = 640,
                Height = 300,
                Items =
                {
                    new TabItem { Header = "One", Content = viewport },
                },
            });

            Arrange(tabs);

            Assert.InRange(viewport.ActualWidth, 639, 641);
            Assert.InRange(card.ActualWidth, 629, 631);
            Assert.Equal(0, card.Margin.Left);
            Assert.Equal(0, card.Margin.Right);
        });
    }

    [Fact]
    public void RoutedView_StretchesWithTheShellAtMinimumNormalAndExpandedSizes()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            window.ShowInTaskbar = false;
            window.Show();
            try
            {
                var sizes = new[]
                {
                    new Size(window.MinWidth, window.MinHeight),
                    new Size(Math.Min(1180, SystemParameters.WorkArea.Width),
                             Math.Min(900, SystemParameters.WorkArea.Height)),
                    new Size(Math.Min(1400, SystemParameters.WorkArea.Width),
                             Math.Min(900, SystemParameters.WorkArea.Height)),
                };

                foreach (var size in sizes)
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.UpdateLayout();
                    var view = Assert.IsAssignableFrom<FrameworkElement>(window.Router.CurrentView);

                    Assert.InRange(
                        Math.Abs(view.ActualWidth - window.ContentHostElement.ActualWidth),
                        0,
                        0.5);
                    Assert.InRange(
                        Math.Abs(view.ActualHeight - window.ContentHostElement.ActualHeight),
                        0,
                        0.5);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NavigationAndAsyncContentGrowth_NeverResizeTheShell()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var document = new StackPanel();
            var createCount = 0;
            fixture.Gate.Register(Fake.Descriptor(
                "fluid-document",
                create: _ =>
                {
                    createCount++;
                    return WithResources(new SettingViewport
                    {
                        Mode = SettingViewportMode.Document,
                        Content = document,
                    });
                }));
            fixture.Main.Pump();
            Assert.Equal(0, createCount);

            var window = fixture.CreateWindow();
            window.ShowInTaskbar = false;
            window.Show();
            try
            {
                window.UpdateLayout();
                var before = new Size(window.ActualWidth, window.ActualHeight);

                window.Router.Navigate("fluid-document");
                window.UpdateLayout();
                Assert.Equal(1, createCount);
                Assert.Equal(before, new Size(window.ActualWidth, window.ActualHeight));

                document.Children.Add(new Border { Height = 5_000 });
                window.UpdateLayout();
                Assert.Equal(before, new Size(window.ActualWidth, window.ActualHeight));

                window.Router.Navigate(Router.FallbackRoute);
                window.UpdateLayout();
                Assert.Equal(before, new Size(window.ActualWidth, window.ActualHeight));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StartupBounds_CenterClampAndHonorNegativeMonitorCoordinates()
    {
        Assert.Equal(1180, Defaults.All["WindowW"].Number);
        Assert.Equal(900, Defaults.All["WindowH"].Number);

        var normal = WindowBoundsPolicy.Calculate(
            new Size(1180, 900),
            new Size(900, 560),
            new Rect(0, 0, 1920, 1040));
        var small = WindowBoundsPolicy.Calculate(
            new Size(1180, 900),
            new Size(900, 560),
            new Rect(-1280, 0, 1024, 700));
        var minimum = WindowBoundsPolicy.Calculate(
            new Size(800, 500),
            new Size(900, 560),
            new Rect(0, 0, 1920, 1040));

        Assert.Equal(new Rect(370, 70, 1180, 900), normal);
        Assert.Equal(new Rect(-1280, 0, 1024, 700), small);
        Assert.Equal(new Rect(510, 240, 900, 560), minimum);
    }

    [Fact]
    public void PixelWorkArea_ConvertsToDipsAtPerMonitorDpi()
    {
        var dips = WindowBoundsPolicy.PixelsToDips(
            new Rect(-1920, 0, 1920, 1080),
            144,
            144);

        Assert.Equal(new Rect(-1280, 0, 1280, 720), dips);
    }

    private static T WithResources<T>(T element) where T : FrameworkElement
    {
        element.Resources.MergedDictionaries.Add(new SettingResources());
        return element;
    }

    private static void Arrange(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.ApplyTemplate();
        element.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
