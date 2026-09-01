using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
