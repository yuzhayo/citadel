using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using Citadel.Setting;
using Citadel.Setting.Components;

namespace Citadel.Uia;

/// <summary>
/// Pins shared presentation behaviour that feature screens must not restate.
/// These tests inspect the rendered template, not a screen-specific wrapper.
/// </summary>
public class SharedComponentBehaviorTests
{
    [Fact]
    public void Field_TextAndPlaceholder_AreLeftAndVerticallyCentered()
    {
        Sta.Run(() =>
        {
            var field = Arrange(new SettingField
            {
                Placeholder = "Search",
                Width = 240,
                Height = 40,
            });

            var editor = Descendant<TextBox>(field);
            var placeholder = Descendants<TextBlock>(field)
                .Single(block => block.Text == "Search");

            Assert.Equal(HorizontalAlignment.Left, editor.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, editor.VerticalContentAlignment);
            Assert.Equal(HorizontalAlignment.Left, placeholder.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, placeholder.VerticalAlignment);
        });
    }

    [Fact]
    public void PasswordField_FillsAvailableWidthAndAcceptsLeftCenteredInput()
    {
        Sta.Run(() =>
        {
            var field = Arrange(new SettingPasswordField
            {
                Width = 240,
                Height = 40,
            });

            var surface = Descendants<Border>(field)
                .Single(border => border.Name == "Surface");
            var editor = Descendant<PasswordBox>(field);

            Assert.Equal(240, surface.ActualWidth);
            Assert.Equal(40, surface.ActualHeight);
            Assert.Equal(HorizontalAlignment.Left, editor.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, editor.VerticalContentAlignment);

            editor.Password = "local-test-password";
            Assert.Equal("local-test-password", field.Password);
        });
    }

    [Fact]
    public void Table_DefaultsToEqualStarColumnsAndCenteredHeaders()
    {
        Sta.Run(() =>
        {
            var table = Arrange(new SettingTable { Width = 640, Height = 240 });
            table.SetColumns(["One", "Two", "Three"]);
            table.SetRows([["a", "b", "c"]]);
            Arrange(table);

            var grid = Descendant<DataGrid>(table);

            Assert.Equal(DataGridLengthUnitType.Star, grid.ColumnWidth.UnitType);
            Assert.All(grid.Columns, column =>
            {
                Assert.Equal(DataGridLengthUnitType.Star, column.Width.UnitType);
                Assert.Equal(1, column.Width.Value);
            });

            var headerStyle = Assert.IsType<Style>(
                grid.Resources[typeof(DataGridColumnHeader)]);
            var alignment = headerStyle.Setters
                .OfType<Setter>()
                .Single(setter => setter.Property == Control.HorizontalContentAlignmentProperty);
            Assert.Equal(HorizontalAlignment.Center, alignment.Value);
        });
    }

    [Fact]
    public void ScrollBar_SharedTemplateActuallyUsed()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 500, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            var vertical = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_VerticalScrollBar");
            Assert.Equal("VerticalScrollBar", AutomationProperties.GetAutomationId(vertical));
            Assert.Equal(Orientation.Vertical, vertical.Orientation);
            Assert.Equal(10, vertical.ActualWidth);
        });
    }

    [Fact]
    public void ScrollBar_VerticalAndHorizontalOrientationCorrect()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 500, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            var vertical = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_VerticalScrollBar");
            var horizontal = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_HorizontalScrollBar");

            Assert.Equal(Orientation.Vertical, vertical.Orientation);
            Assert.Equal(Orientation.Horizontal, horizontal.Orientation);
            Assert.Equal(10, vertical.ActualWidth);
            Assert.Equal(10, horizontal.ActualHeight);

            var verticalDecrease = Descendants<RepeatButton>(vertical)
                .Single(button => button.Name == "PART_DecreaseButton");
            var verticalIncrease = Descendants<RepeatButton>(vertical)
                .Single(button => button.Name == "PART_IncreaseButton");
            var horizontalDecrease = Descendants<RepeatButton>(horizontal)
                .Single(button => button.Name == "PART_DecreaseButton");
            var horizontalIncrease = Descendants<RepeatButton>(horizontal)
                .Single(button => button.Name == "PART_IncreaseButton");

            Assert.Same(ScrollBar.PageUpCommand, verticalDecrease.Command);
            Assert.Same(ScrollBar.PageDownCommand, verticalIncrease.Command);
            Assert.Same(ScrollBar.PageLeftCommand, horizontalDecrease.Command);
            Assert.Same(ScrollBar.PageRightCommand, horizontalIncrease.Command);
        });
    }

    [Fact]
    public void ScrollBar_SessionDetachesAndReattachesAcrossLifecycle()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 180, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            scrollViewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(ScrollBarAutoFade.HasActiveSession(scrollViewer));

            scrollViewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.False(ScrollBarAutoFade.HasActiveSession(scrollViewer));

            scrollViewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(ScrollBarAutoFade.HasActiveSession(scrollViewer));

            ScrollBarAutoFade.SetIsEnabled(scrollViewer, false);
            Assert.False(ScrollBarAutoFade.HasActiveSession(scrollViewer));
        });
    }

    [Fact]
    public void ScrollBar_LayoutWidthStableDuringOpacityChange()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 180, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            var presenter = Descendants<ScrollContentPresenter>(scrollViewer).Single();
            var vertical = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_VerticalScrollBar");
            var baselineWidth = presenter.ActualWidth;

            vertical.Opacity = 1.0;
            Arrange(scrollViewer);
            Assert.Equal(baselineWidth, presenter.ActualWidth);

            vertical.Opacity = 0.0;
            Arrange(scrollViewer);
            Assert.Equal(baselineWidth, presenter.ActualWidth);
        });
    }

    [Fact]
    public void SharedResourceKeys_RemainBackwardCompatible()
    {
        Sta.Run(() =>
        {
            var resources = new SettingResources();

            Assert.IsType<Style>(resources["SettingScrollBarStyle"]);
            Assert.IsType<Style>(resources["SettingScrollViewerStyle"]);
            Assert.IsType<Style>(resources["SettingComboBoxItemStyle"]);
            Assert.IsType<Style>(resources["SettingComboBoxStyle"]);
            Assert.IsType<Style>(resources["SettingListStyle"]);
        });
    }

    private static T WithResources<T>(T element) where T : FrameworkElement
    {
        element.Resources.MergedDictionaries.Add(new SettingResources());
        return element;
    }

    private static T Arrange<T>(T element) where T : FrameworkElement
    {
        var width = double.IsNaN(element.Width) ? 640 : element.Width;
        var height = double.IsNaN(element.Height) ? 240 : element.Height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.ApplyTemplate();
        element.UpdateLayout();
        return element;
    }

    private static T Descendant<T>(DependencyObject root) where T : DependencyObject =>
        Descendants<T>(root).Single();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
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
