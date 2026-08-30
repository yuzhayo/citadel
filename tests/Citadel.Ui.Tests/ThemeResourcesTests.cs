using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Ui.Theme;

namespace Citadel.Ui.Tests;

public sealed class ThemeResourcesTests
{
    [Fact]
    public void DynamicMetricAndBrushConsumersChangeLive()
    {
        StaTest.Run(() =>
        {
            var tokens = new Tokens();
            var lifetime = new Lifetime();
            var resources = new ThemeResources();
            resources.Bind(tokens, lifetime);

            var border = new Border();
            border.Resources.MergedDictionaries.Add(resources);
            border.SetResourceReference(FrameworkElement.WidthProperty, "Rail");
            border.SetResourceReference(Border.BackgroundProperty, "Hover");

            Assert.Equal(58d, border.Width, precision: 6);
            Assert.Equal(Color.FromRgb(0x23, 0x23, 0x20), Assert.IsType<SolidColorBrush>(border.Background).Color);

            Assert.True(tokens.CommitCore("Rail", JsonValue.Create(72)).Applied);
            Assert.True(tokens.CommitCore("Hover", JsonValue.Create("#123456")).Applied);

            Assert.Equal(72d, border.Width, precision: 6);
            Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), Assert.IsType<SolidColorBrush>(border.Background).Color);

            lifetime.Destroy();
            Assert.True(tokens.CommitCore("Rail", JsonValue.Create(80)).Applied);
            Assert.Equal(72d, border.Width, precision: 6);
        });
    }

    [Fact]
    public void TemplatesUseDynamicResourcesAndRailButtonMapsHoverAndPressedTokens()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var theme = File.ReadAllText(Path.Combine(
            repoRoot, "core", "Citadel.Ui", "Theme", "ThemeResources.xaml"));
        var railButton = File.ReadAllText(Path.Combine(
            repoRoot, "core", "Citadel.Ui", "Controls", "RailButton.xaml"));

        Assert.DoesNotContain("StaticResource", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("StaticResource", railButton, StringComparison.Ordinal);
        Assert.Contains("ShellCardStyle", theme, StringComparison.Ordinal);
        Assert.Contains("AppHeaderHeightGridLength", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("PART_HeaderTitleHost", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("PART_CollapseToggle", theme, StringComparison.Ordinal);
        Assert.Matches(
            new Regex("IsMouseOver[\\s\\S]*?DynamicResource Hover", RegexOptions.CultureInvariant),
            railButton);
        Assert.Matches(
            new Regex("IsPressed[\\s\\S]*?DynamicResource Selected", RegexOptions.CultureInvariant),
            railButton);
    }
}
