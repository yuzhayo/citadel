using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Shell;

namespace Citadel.Uia;

/// <summary>
/// The seam that lets a module have an editable layout without reading tokens
/// itself. Pinned against a fake view here and exercised by detachable citizens.
/// </summary>
public class LayoutApplierTests
{
    /// <summary>
    /// A view built in code needs an explicit NameScope before RegisterName
    /// works; XAML-loaded views get one for free. FindName is how the applier
    /// locates a slot, so the fake has to be name-resolvable the same way.
    /// </summary>
    private static (FrameworkElement Root, Border Slot) FakeView(string slotName)
    {
        var slot = new Border { Name = slotName, Width = 100, Height = 50 };
        var panel = new Grid();
        NameScope.SetNameScope(panel, new NameScope());
        panel.Children.Add(slot);
        panel.RegisterName(slotName, slot);
        return (panel, slot);
    }

    [Fact]
    public void DeclaredDefaults_ApplyWithNoOverride()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("poolTable");
            var tokens = Fake.Store();
            var declaration = Fake.Declaration(
                """{ "poolTable": { "kind": "size", "w": 640, "h": 380 } }""");

            LayoutApplier.Apply(root, "gateway", declaration, tokens);

            Assert.Equal(640, slot.Width);
            Assert.Equal(380, slot.Height);
        });
    }

    [Fact]
    public void SparseOverride_WinsOverTheDeclaredDefault()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("poolTable");
            var tokens = Fake.Store();
            Assert.True(tokens.CommitLayout("gateway", "poolTable", "w", JsonValue.Create(720)));
            var declaration = Fake.Declaration(
                """{ "poolTable": { "kind": "size", "w": 640, "h": 380 } }""");

            LayoutApplier.Apply(root, "gateway", declaration, tokens);

            Assert.Equal(720, slot.Width);
            Assert.Equal(380, slot.Height); // untouched property keeps its default
        });
    }

    /// <summary>
    /// Position is a delta through a transform, not an absolute coordinate: the
    /// view already placed the element, so an absolute offset would count the
    /// declared default twice.
    /// </summary>
    [Fact]
    public void Position_IsResolvedMinusDeclared_ThroughATransform()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("statusPill");
            var tokens = Fake.Store();
            var declaration = Fake.Declaration(
                """{ "statusPill": { "kind": "position", "x": 16, "y": 8 } }""");

            LayoutApplier.Apply(root, "gateway", declaration, tokens);

            Assert.False(slot.RenderTransform is TranslateTransform);

            Assert.True(tokens.CommitLayout(
                "gateway", "statusPill", "x", JsonValue.Create(20)));
            LayoutApplier.Apply(root, "gateway", declaration, tokens);

            var transform = Assert.IsType<TranslateTransform>(slot.RenderTransform);
            Assert.Equal(4, transform.X);
            Assert.Equal(0, transform.Y);
            Assert.Equal(100, slot.Width); // position never touches size
        });
    }

    [Fact]
    public void Visibility_AppliesOnlyForABoolean()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("heartbeat");
            var tokens = Fake.Store();

            LayoutApplier.Apply(
                root,
                "gateway",
                Fake.Declaration("""{ "heartbeat": { "kind": "visibility", "visible": false } }"""),
                tokens);
            Assert.Equal(Visibility.Collapsed, slot.Visibility);

            LayoutApplier.Apply(
                root,
                "gateway",
                Fake.Declaration("""{ "heartbeat": { "kind": "visibility", "visible": "maybe" } }"""),
                tokens);
            Assert.Equal(Visibility.Collapsed, slot.Visibility); // a string is not a visibility
        });
    }

    /// <summary>
    /// Only the properties a slot's kind permits. The sanitizer sees
    /// property names and JSON types; the declared kind is what makes this
    /// checkable, so the shell is where it is checked.
    /// </summary>
    [Fact]
    public void PropertiesForeignToTheKind_AreIgnored()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("statusPill");
            var tokens = Fake.Store();
            var declaration = Fake.Declaration(
                """{ "statusPill": { "kind": "position", "x": 4, "w": 999, "visible": false } }""");

            LayoutApplier.Apply(root, "gateway", declaration, tokens);

            Assert.Equal(100, slot.Width);
            Assert.Equal(Visibility.Visible, slot.Visibility);
            Assert.False(slot.RenderTransform is TranslateTransform);
        });
    }

    [Fact]
    public void NonPositiveSize_IsIgnored_SoASlotCannotBeErased()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("poolTable");
            var tokens = Fake.Store();
            var declaration = Fake.Declaration(
                """{ "poolTable": { "kind": "size", "w": 0, "h": -5 } }""");

            LayoutApplier.Apply(root, "gateway", declaration, tokens);

            Assert.Equal(100, slot.Width);
            Assert.Equal(50, slot.Height);
        });
    }

    [Fact]
    public void UnknownSlotOrKind_IsSkippedWithoutThrowing()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("poolTable");
            var tokens = Fake.Store();
            var declaration = Fake.Declaration(
                """
                {
                  "ghost": { "kind": "size", "w": 300 },
                  "poolTable": { "kind": "typography", "w": 300 }
                }
                """);

            var exception = Record.Exception(
                () => LayoutApplier.Apply(root, "gateway", declaration, tokens));

            Assert.Null(exception);
            Assert.Equal(100, slot.Width);
        });
    }

    [Fact]
    public void Attach_ReappliesOnTokensChanged_AndStopsWhenTheLifetimeDies()
    {
        Sta.Run(() =>
        {
            var (root, slot) = FakeView("poolTable");
            var tokens = Fake.Store();
            var declaration = Fake.Declaration(
                """{ "poolTable": { "kind": "size", "w": 640 } }""");
            var lifetime = new Lifetime();

            LayoutApplier.Attach(root, "gateway", declaration, tokens, lifetime);
            Assert.Equal(640, slot.Width);

            Assert.True(tokens.CommitLayout("gateway", "poolTable", "w", JsonValue.Create(720)));
            Assert.Equal(720, slot.Width);

            lifetime.Destroy();
            Assert.True(tokens.CommitLayout("gateway", "poolTable", "w", JsonValue.Create(500)));
            Assert.Equal(720, slot.Width); // nav-away stopped the subscription
        });
    }

    [Fact]
    public void Declaration_DeepClones_SoNoPartyCanMutateAnother()
    {
        var slots = JsonNode.Parse("""{ "poolTable": { "kind": "size", "w": 640 } }""")!.AsObject();
        var declaration = new LayoutDeclaration(slots);

        slots["poolTable"]!["w"] = 1;
        declaration.Slots["poolTable"]!["w"] = 2;
        declaration.Slot("poolTable")!["w"] = 3;

        Assert.Equal(640, declaration.Slot("poolTable")!["w"]!.GetValue<double>());
        Assert.Equal("size", declaration.KindOf("poolTable"));
        Assert.Equal(["poolTable"], declaration.SlotNames);
    }

    [Fact]
    public void Router_AppliesLayoutToACitizenViewWithoutTheModuleReadingTokens()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            Border? slot = null;
            Assert.True(shell.Tokens.CommitLayout("alpha", "panel", "w", JsonValue.Create(321)));

            shell.Gate.Register(Fake.Descriptor(
                "alpha",
                create: _ =>
                {
                    slot = new Border { Name = "panel" };
                    var host = new Grid();
                    NameScope.SetNameScope(host, new NameScope());
                    host.Children.Add(slot);
                    host.RegisterName("panel", slot);
                    return host;
                },
                layout: Fake.Declaration("""{ "panel": { "kind": "size", "w": 100 } }""")));
            shell.Main.Pump();

            shell.Router.Navigate("alpha");

            Assert.NotNull(slot);
            Assert.Equal(321, slot!.Width);
        });
    }
}
