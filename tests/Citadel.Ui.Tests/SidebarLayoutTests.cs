using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Ui.Animations;
using Citadel.Ui.Controls;

namespace Citadel.Ui.Tests;

public sealed class SidebarLayoutTests
{
    public static TheoryData<double, double, double> GuardPermittedGeometry => new()
    {
        { 58, 20, 49 },
        { 24, 8, 24 },
        { 120, 64, 1 },
        { 75, 100, 500 }, // TitleX is guard-clamped to Rail.
    };

    [Theory]
    [MemberData(nameof(GuardPermittedGeometry))]
    public void ComputedNavIconAndTitleCoordinatesStayTokenAligned(
        double rail,
        double iconSlot,
        double requestedTitleX)
    {
        StaTest.Run(() =>
        {
            var tokens = new Tokens();
            CommitNumber(tokens, "Rail", rail);
            CommitNumber(tokens, "IconSlot", iconSlot);
            CommitNumber(tokens, "TitleX", requestedTitleX);
            Assert.InRange(tokens.Number("TitleX"), double.Epsilon, tokens.Number("Rail"));

            var clock = new FakeFrameClock();
            using var animations = new AnimationManager(clock);
            var lifetime = new Lifetime();
            var sidebar = new Sidebar();
            sidebar.Entries.Add(new NavEntry("home", "Home", "\uE80F"));
            sidebar.Attach(tokens, animations, lifetime);

            WpfLayout.Arrange(sidebar, sidebar.CurrentWidth);

            var navList = Assert.IsType<ListBox>(
                sidebar.Template.FindName(Sidebar.NavListPart, sidebar));
            var row = Assert.IsType<ListBoxItem>(navList.ItemContainerGenerator.ContainerFromIndex(0));
            var icon = Assert.Single(
                WpfLayout.Descendants<FrameworkElement>(row),
                element => Equals(element.Tag, "NavIcon"));
            var navTitleHost = Assert.Single(
                WpfLayout.Descendants<Border>(row),
                element => Equals(element.Tag, "NavTitleHost"));
            var iconCenter = WpfLayout.CenterIn(icon, sidebar);
            var navTitleX = navTitleHost.TransformToAncestor(sidebar).Transform(new Point()).X;
            Assert.Equal(tokens.Number("Rail") / 2d, iconCenter.X, precision: 6);
            Assert.Equal(tokens.Number("TitleX"), navTitleX, precision: 6);
            Assert.Equal(tokens.Number("IconSlot"), icon.ActualWidth, precision: 6);

            lifetime.Destroy();
        });
    }

    [Fact]
    public void RowHeightComesOnlyFromRowAndTitlesRemainInLayoutWhenCollapsed()
    {
        StaTest.Run(() =>
        {
            var tokens = new Tokens();
            CommitNumber(tokens, "Row", 42);
            var clock = new FakeFrameClock();
            using var animations = new AnimationManager(clock);
            var lifetime = new Lifetime();
            var sidebar = new Sidebar { TitleFontSize = 72 };
            sidebar.Entries.Add(new NavEntry("ok", "OK", "\uE80F"));
            sidebar.Entries.Add(new NavEntry(
                "long",
                "Forty-character-title-that-cannot-drive-height",
                "\uE8A5"));
            sidebar.Attach(tokens, animations, lifetime);

            AssertRows(tokens.Number("Row"), sidebar, collapsed: false);
            sidebar.SetCollapsed(true, animate: false);
            AssertRows(tokens.Number("Row"), sidebar, collapsed: true);

            lifetime.Destroy();
        });
    }

    [Fact]
    public void FullWidthRecomputesForEntriesTokensAndTypographyWithoutARegistry()
    {
        StaTest.Run(() =>
        {
            var tokens = new Tokens();
            var clock = new FakeFrameClock();
            using var animations = new AnimationManager(clock);
            var lifetime = new Lifetime();
            var sidebar = new Sidebar();
            sidebar.Attach(tokens, animations, lifetime);

            Assert.Empty(sidebar.MainEntries);
            Assert.Equal(NavEntry.Settings, Assert.Single(sidebar.PinnedEntries));
            AssertWidthMatchesPureCalculation(sidebar, tokens);

            sidebar.Entries.Add(new NavEntry(
                "reports",
                "Reports and operational history",
                "\uE9D2"));
            AssertWidthMatchesPureCalculation(sidebar, tokens);

            CommitNumber(tokens, "FullDefault", 280);
            AssertWidthMatchesPureCalculation(sidebar, tokens);
            Assert.Equal(280d, sidebar.FullWidth, precision: 6);

            sidebar.TitleFontSize = 48;
            AssertWidthMatchesPureCalculation(sidebar, tokens);
            Assert.Equal(tokens.Number("FullMax"), sidebar.FullWidth, precision: 6);

            sidebar.SetCollapsed(true, animate: false);
            CommitNumber(tokens, "Rail", 72);
            AssertWidthMatchesPureCalculation(sidebar, tokens);
            Assert.Equal(72d, sidebar.CurrentWidth, precision: 6);

            lifetime.Destroy();
        });
    }

    private static void AssertRows(double expectedHeight, Sidebar sidebar, bool collapsed)
    {
        WpfLayout.Arrange(sidebar, sidebar.CurrentWidth);
        var navList = Assert.IsType<ListBox>(
            sidebar.Template.FindName(Sidebar.NavListPart, sidebar));

        for (var index = 0; index < sidebar.MainEntries.Count; index++)
        {
            var row = Assert.IsType<ListBoxItem>(navList.ItemContainerGenerator.ContainerFromIndex(index));
            Assert.Equal(expectedHeight, row.ActualHeight, precision: 6);

            var titleHost = Assert.Single(
                WpfLayout.Descendants<Border>(row),
                element => Equals(element.Tag, "NavTitleHost"));
            Assert.Equal(Visibility.Visible, titleHost.Visibility);
            Assert.True(titleHost.ClipToBounds);
            Assert.Equal(collapsed ? 0d : 1d, titleHost.Opacity, precision: 6);
        }
    }

    private static void AssertWidthMatchesPureCalculation(Sidebar sidebar, Tokens tokens)
    {
        var expected = Sidebar.CalculateFullWidth(
            sidebar.MainEntries.Concat(sidebar.PinnedEntries),
            tokens,
            sidebar.TitleFontFamily,
            sidebar.TitleFontSize,
            VisualTreeHelper.GetDpi(sidebar).PixelsPerDip);
        Assert.Equal(expected, sidebar.FullWidth, precision: 6);
        if (!sidebar.IsCollapsed) Assert.Equal(expected, sidebar.CurrentWidth, precision: 6);
    }

    private static void CommitNumber(Tokens tokens, string token, double value)
    {
        var result = tokens.CommitCore(token, JsonValue.Create(value));
        Assert.True(result.Applied);
    }
}
