using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Setting;
using Citadel.Setting.Components;
using Citadel.Setting.LayoutEditor;
using Citadel.Setting.Screens;
using TokensEngine = Citadel.Core.Tokens.Tokens;

namespace Citadel.Uia;

/// <summary>
/// Settings-screen behavior. Every persistence assertion goes
/// through a **fresh Tokens instance reading the same file** — that is what
/// "restart" means here, and a mock would prove nothing.
/// </summary>
public class SettingScreensTests
{
    private static string NewStorePath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "citadel-setting-" + Guid.NewGuid().ToString("N"),
        "ui.json");

    /// <summary>A colour and a position edited from the UI both persist.</summary>
    [Fact]
    public void G1_ColourAndPosition_PersistSparselyAcrossARestart()
    {
        Sta.Run(() =>
        {
            var path = NewStorePath();
            var tokens = new TokensEngine(path);
            var host = new StubSettingHost();
            using var lifetime = new LifetimeScope();

            var appearance = new AppearanceScreen(tokens, lifetime.Value);
            appearance.SetColour("BgRail", "#0E0E0D");

            var declaration = Fake.Declaration(
                """{ "statusPill": { "kind": "position", "x": 16, "y": 8 } }""");
            host.SetScreens(Fake.Descriptor("gateway", "Gateway", layout: declaration));
            var layout = new ModuleLayoutScreen(host, tokens, null, lifetime.Value);
            layout.Select("gateway");
            layout.SetNumber("statusPill", "x", 40);

            var reloaded = new TokensEngine(path);
            reloaded.Load();

            Assert.Equal(0xFF0E0E0Du, reloaded.Color("BgRail"));
            Assert.Equal(40, reloaded.LayoutOverrides("gateway")!["statusPill"]!["x"]!.GetValue<double>());

            // Sparse: only what changed is on disk.
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var core = root["themes"]!["default"]!["core"]!.AsObject();
            Assert.Equal(["BgRail"], core.Select(pair => pair.Key));
            var slots = root["themes"]!["default"]!["module"]!["gateway"]!["statusPill"]!.AsObject();
            Assert.Equal(["x"], slots.Select(pair => pair.Key));
        });
    }

    /// <summary>
    /// A contrast failure warns, stays applied, and offers reset-this-token.
    /// Contrast warns; it never blocks (Guard.cs).
    /// </summary>
    [Fact]
    public void G2_ContrastFailure_WarnsVisibly_AndOffersResetThisToken()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            // Fg == Bg is a ratio of 1.0.
            appearance.SetColour("Fg", "#171716");

            Assert.Contains(appearance.LastIssues, issue => issue.Verdict == GuardVerdict.Warned);
            Assert.Contains(appearance.VisibleIssues, text => text.Contains("Warned"));
            Assert.Equal(0xFF171716u, tokens.Color("Fg")); // warned, still applied

            Assert.True(appearance.ResetFromIssue("Fg"));
            Assert.Equal(Defaults.All["Fg"], tokens.Resolve("Fg"));
        });
    }

    /// <summary>
    /// A non-positive size returns to the declared slot
    /// default with a warning.
    /// </summary>
    [Fact]
    public void G3_ZeroWidth_ReturnsToTheDeclaredDefault()
    {
        Sta.Run(() =>
        {
            var path = NewStorePath();
            var tokens = new TokensEngine(path);
            var host = new StubSettingHost();
            using var lifetime = new LifetimeScope();
            var declaration = Fake.Declaration(
                """{ "poolTable": { "kind": "size", "w": 640, "h": 380 } }""");
            host.SetScreens(Fake.Descriptor("gateway", "Gateway", layout: declaration));
            var screen = new ModuleLayoutScreen(host, tokens, null, lifetime.Value);
            screen.Select("gateway");

            screen.SetNumber("poolTable", "w", 0);

            Assert.Equal("640", screen.Find("poolTable", "w")!.Text);
            Assert.Contains("restored declared default 640", screen.Status);

            var reloaded = new TokensEngine(path);
            reloaded.Load();
            Assert.Equal(
                640,
                reloaded.LayoutOverrides("gateway")!["poolTable"]!["w"]!.GetValue<double>());
        });
    }

    /// <summary>Switching theme runs the guard and the UI shows the result.</summary>
    [Fact]
    public void G4_ThemeActivation_SurfacesGuardIssues()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();

            // A theme carrying a contrast-failing pair must not walk past silently.
            tokens.CreateTheme("flat");
            tokens.ActivateTheme("flat");
            tokens.CommitCore("Fg", JsonValue.Create("#171716"));
            tokens.ActivateTheme(TokensEngine.DefaultThemeName);

            var appearance = new AppearanceScreen(tokens, lifetime.Value);
            appearance.ActivateTheme("flat");

            Assert.Contains(appearance.LastIssues, issue => issue.Verdict == GuardVerdict.Warned);
            Assert.Contains(appearance.VisibleIssues, text => text.Contains("Warned"));
        });
    }

    /// <summary>
    /// A Gallery setup survives a fresh reader, and an unknown id falls back
    /// to the plain control with a warning.
    /// </summary>
    [Fact]
    public void G5_GallerySetupSurvivesRestart_AndUnknownIdFallsBack()
    {
        Sta.Run(() =>
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "citadel-presets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var lifetime = new LifetimeScope();
            var host = new StubSettingHost();

            var gallery = new GalleryScreen(host, lifetime.Value, directory);
            gallery.SelectControl("Field");
            gallery.SetValue("placeholder", "Search");
            gallery.SetValue("width", "320");

            var preview = Assert.IsType<SettingField>(gallery.Sample);
            Assert.Equal("Search", preview.Placeholder);
            Assert.Equal(320, preview.Width);

            Assert.True(gallery.Save("wide-field", "Wide field"));
            Assert.DoesNotContain("NOT saved", gallery.Status);

            // A fresh reader over the same folder: the setup is really on disk.
            var reloaded = PresetStore.Load("Field", directory);
            Assert.True(reloaded.Contains("wide-field"));

            var plain = new SettingField();
            Assert.True(PresetApplier.Apply(plain, reloaded, "wide-field"));
            Assert.Equal("Search", plain.Placeholder);
            Assert.Equal(320, plain.Width);

            // Unknown id: plain control, no exception.
            var untouched = new SettingField();
            Assert.False(PresetApplier.Apply(untouched, reloaded, "no-such-setup"));
            Assert.Equal(string.Empty, untouched.Placeholder);
        });
    }

    /// <summary>
    /// The editor is generated from a **fake** descriptor, one control per
    /// slot of the right kind, and editing one writes a sparse override by route.
    /// </summary>
    [Fact]
    public void G6_EditorIsGeneratedFromAFakeDescriptor_AndWritesByRoute()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            var host = new StubSettingHost();
            using var lifetime = new LifetimeScope();

            host.SetScreens(Fake.Descriptor("gateway", "Gateway", layout: Fake.Declaration(
                """
                {
                  "poolTable":  { "kind": "size",       "w": 640, "h": 380 },
                  "statusPill": { "kind": "position",   "x": 16,  "y": 8 },
                  "heartbeat":  { "kind": "visibility", "visible": true }
                }
                """)));

            var screen = new ModuleLayoutScreen(host, tokens, null, lifetime.Value);
            screen.Select("gateway");

            var editor = Assert.IsAssignableFrom<FrameworkElement>(screen.Editor);
            Assert.Equal("LayoutEditor:gateway", AutomationProperties.GetAutomationId(editor));

            // A control per declared property, of the kind the slot declares.
            Assert.NotNull(screen.Find("poolTable", "w"));
            Assert.NotNull(screen.Find("poolTable", "h"));
            Assert.NotNull(screen.Find("statusPill", "x"));
            Assert.NotNull(screen.Find("statusPill", "y"));
            Assert.NotNull(screen.FindToggle("heartbeat"));

            // position has no w/h control, size has no visible toggle.
            Assert.Null(screen.Find("statusPill", "w"));
            Assert.Null(screen.FindToggle("poolTable"));

            screen.SetNumber("poolTable", "w", 720);

            var overrides = tokens.LayoutOverrides("gateway")!;
            Assert.Equal(720, overrides["poolTable"]!["w"]!.GetValue<double>());
            Assert.Null(tokens.LayoutOverrides("other-route"));
        });
    }

    /// <summary>The app must be usable with nothing installed.</summary>
    [Fact]
    public void EmptyRegistry_LeavesSettingsFullyUsable()
    {
        Sta.Run(() =>
        {
            using var lifetime = new LifetimeScope();
            var host = new StubSettingHost();

            var settings = new SettingsScreen(host, lifetime.Value);

            Assert.Empty(settings.ScreenTable.Rows);
            Assert.Equal(["Nothing failed."], settings.VisibleFailures);
        });
    }

    [Fact]
    public void SettingsScreen_ListsScreensAndSurfacesFailures()
    {
        Sta.Run(() =>
        {
            using var lifetime = new LifetimeScope();
            var host = new StubSettingHost();
            var settings = new SettingsScreen(host, lifetime.Value);

            host.SetScreens(
                Fake.Descriptor("gateway", "Gateway", order: 10),
                Fake.Descriptor("blank", "Blank", order: 999));
            host.SetFailures(new ScreenFailure("broken", "entry assembly missing"));

            Assert.Equal(2, settings.ScreenTable.Rows.Count);
            Assert.Contains(
                settings.VisibleFailures,
                text => text.Contains("broken") && text.Contains("entry assembly missing"));
        });
    }

    /// <summary>
    /// The three sub-screens are requested from inside Settings through the host
    /// seam. Shell decides how the separate editor window is presented.
    /// </summary>
    [Fact]
    public void SettingsScreen_RequestsTheThreeSubScreens()
    {
        Sta.Run(() =>
        {
            using var lifetime = new LifetimeScope();
            var host = new StubSettingHost();
            var settings = new SettingsScreen(host, lifetime.Value);

            settings.Click("OpenAppearance");
            settings.Click("OpenLayout");
            settings.Click("OpenGallery");

            Assert.Equal(
                [SettingsScreen.AppearanceRoute, SettingsScreen.LayoutRoute, SettingsScreen.GalleryRoute],
                host.OpenedSettings);
        });
    }

    [Fact]
    public void UpdateModules_GoesThroughTheSeam_WithoutASearcher()
    {
        Sta.Run(() =>
        {
            using var lifetime = new LifetimeScope();
            var host = new StubSettingHost();
            var settings = new SettingsScreen(host, lifetime.Value);

            settings.Click("UpdateModules");

            Assert.Equal(1, host.RediscoveryRequests);
        });
    }

    [Fact]
    public void UpdateCard_MirrorsHostState_AndUsesTheTwoUpdateActions()
    {
        Sta.Run(() =>
        {
            using var lifetime = new LifetimeScope();
            var host = new StubSettingHost();
            var settings = new SettingsScreen(host, lifetime.Value);

            host.SetUpdateState(new AppUpdateState(
                "0.1.0",
                "0.2.0",
                "Version 0.2.0 is available.",
                0,
                true,
                true,
                true,
                false,
                true));

            Assert.Equal("Version 0.1.0", settings.VisibleUpdateVersion);
            Assert.Equal("Version 0.2.0 is available.", settings.VisibleUpdateStatus);
            Assert.True(settings.CheckUpdatesEnabled);
            Assert.Equal(Visibility.Visible, settings.InstallUpdateVisibility);

            settings.Click("CheckUpdates");
            settings.Click("InstallUpdate");

            Assert.Equal(1, host.UpdateChecks);
            Assert.Equal(1, host.UpdateInstalls);
        });
    }

    /// <summary>
    /// Settings' own layout is editable, keyed by route `settings`, without ever
    /// being registered as a citizen.
    /// </summary>
    [Fact]
    public void SettingsOwnLayout_IsEditableWithoutRegisteringSettings()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            var host = new StubSettingHost();
            using var lifetime = new LifetimeScope();

            var own = Fake.Declaration("""{ "actions": { "kind": "position", "x": 0, "y": 0 } }""");
            var screen = new ModuleLayoutScreen(host, tokens, own, lifetime.Value);

            Assert.Equal(ModuleLayoutScreen.SettingsRoute, screen.SelectedRoute);
            screen.SetNumber("actions", "y", 12);

            Assert.Equal(12, tokens.LayoutOverrides("settings")!["actions"]!["y"]!.GetValue<double>());
            Assert.Empty(host.Screens());
        });
    }

    /// <summary>
    /// Live drag: preview per frame, one commit on release, Esc back to the
    /// drag-start value.
    /// </summary>
    [Fact]
    public void Appearance_LiveDrag_PreviewsThenCommitsOnce()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            appearance.BeginDrag("Row");
            appearance.DragTo(48);
            Assert.Equal(48, tokens.Number("Row"));
            Assert.True(tokens.HasPreview);

            appearance.EndDrag(48);
            Assert.False(tokens.HasPreview);
            Assert.Equal(48, tokens.Number("Row"));
        });
    }

    [Fact]
    public void Appearance_RelatedSettingsDetermineEachOthersEditorLimits()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            Assert.Equal((58d, 320d), appearance.MetricRange("FullDefault"));

            appearance.SetMetric("FullDefault", 250);

            Assert.Equal(250, appearance.MetricRange("FullMax").Minimum);
            Assert.Equal(250, appearance.MetricRange("FullMin").Maximum);
            Assert.Equal(250, appearance.MetricRange("Rail").Maximum);

            // FullMax cannot silently pull FullDefault down. Its slider stops at
            // the current FullDefault until the user lowers FullDefault first.
            appearance.SetMetric("FullMax", 200);
            Assert.Equal(250, tokens.Number("FullMax"));
            Assert.Equal(250, tokens.Number("FullDefault"));

            // Text-based window metrics follow the same relationship contract.
            appearance.SetMetric("WindowMinW", 1300);
            Assert.Equal(900, tokens.Number("WindowMinW"));
            Assert.Contains("cannot exceed WindowW", appearance.Status);

            appearance.SetMetric("WindowW", 1400);
            appearance.SetMetric("WindowMinW", 1300);
            Assert.Equal(1300, tokens.Number("WindowMinW"));
            Assert.Equal(1300, appearance.MetricRange("WindowW").Minimum);
        });
    }

    [Fact]
    public void Appearance_EscDuringDrag_RestoresTheDragStartValue()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);
            var start = tokens.Number("Row");

            appearance.BeginDrag("Row");
            appearance.DragTo(90);
            appearance.CancelDrag();

            Assert.False(tokens.HasPreview);
            Assert.Equal(start, tokens.Number("Row"));
        });
    }

    [Fact]
    public void Appearance_ThemeSwitchDuringDrag_CancelsStaleReleaseAndPersistsTheme()
    {
        Sta.Run(() =>
        {
            var path = NewStorePath();
            var tokens = new TokensEngine(path);
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            appearance.BeginDrag("Row");
            appearance.DragTo(90);
            appearance.ActivateTheme("quiet");
            appearance.EndDrag(90); // the old pointer release must be inert

            Assert.False(tokens.HasPreview);
            Assert.Equal("quiet", tokens.ActiveThemeName);
            Assert.Equal(Defaults.All["Row"].Number, tokens.Number("Row"));
            Assert.Equal(Defaults.All["Row"].Number, appearance.MetricValue("Row"));

            var reloaded = new TokensEngine(path);
            reloaded.Load();
            Assert.Equal("quiet", reloaded.ActiveThemeName);
        });
    }

    /// <summary>
    /// A semi-transparent foreground is refused by the editor, because the guard's
    /// luminance ignores alpha and would report a contrast ratio that is a lie.
    /// </summary>
    [Fact]
    public void Appearance_ColourEditor_EmitsOpaqueForegroundsOnly()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            appearance.SetColour("Fg", "#80FFFFFF");

            Assert.Equal(Defaults.All["Fg"], tokens.Resolve("Fg"));
            Assert.Contains("alpha is not editable", appearance.Status);
        });
    }

    /// <summary>A failed persist is shown, never claimed as success.</summary>
    [Fact]
    public void Appearance_SaveFailure_IsVisibleAndNotClaimedAsSuccess()
    {
        Sta.Run(() =>
        {
            var path = NewStorePath();
            var tokens = new TokensEngine(path);
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            appearance.SetColour("BgRail", "#0E0E0D");
            Assert.Equal("Saved.", appearance.Status);

            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                appearance.SetColour("BgRail", "#0A0A09");

                Assert.Contains("NOT saved", appearance.Status);
                Assert.Equal(0xFF0A0A09u, tokens.Color("BgRail")); // live value kept
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        });
    }

    [Fact]
    public void Appearance_ResetEverythingFailure_IsVisibleAndSurvivesOnDisk()
    {
        Sta.Run(() =>
        {
            var path = NewStorePath();
            var tokens = new TokensEngine(path);
            Assert.True(tokens.CommitCore("Rail", JsonValue.Create(64)).Applied);
            Assert.True(tokens.TrySave(out _));

            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                appearance.ResetEverything();

                Assert.Equal(Defaults.All["Rail"].Number, tokens.Number("Rail"));
                Assert.Contains("NOT saved", appearance.Status);

                var reloaded = new TokensEngine(path);
                reloaded.Load();
                Assert.Equal(64, reloaded.Number("Rail"));
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        });
    }

    [Fact]
    public void Appearance_ExposesWindowInitialAndMinimumTokens()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            appearance.SetMetric("WindowW", 1280);
            Assert.Contains("next launch", appearance.Status);
            appearance.SetMetric("WindowH", 800);
            appearance.SetMetric("WindowMinW", 960);
            appearance.SetMetric("WindowMinH", 600);

            Assert.Equal(1280, appearance.MetricValue("WindowW"));
            Assert.Equal(800, appearance.MetricValue("WindowH"));
            Assert.Equal(960, appearance.MetricValue("WindowMinW"));
            Assert.Equal(600, appearance.MetricValue("WindowMinH"));
        });
    }

    /// <summary>
    /// A guard clamp on a token the user did not touch must reach the editor:
    /// narrowing Rail moves TitleX, and the panel follows the store.
    /// </summary>
    [Fact]
    public void Appearance_ShowsAClampOnATokenTheUserDidNotEdit()
    {
        Sta.Run(() =>
        {
            var tokens = new TokensEngine(NewStorePath());
            using var lifetime = new LifetimeScope();
            var appearance = new AppearanceScreen(tokens, lifetime.Value);

            appearance.BeginDrag("Rail");
            appearance.EndDrag(30);

            Assert.Contains(
                appearance.LastIssues,
                issue => issue.Token == "TitleX" && issue.Verdict == GuardVerdict.Clamped);
            Assert.Equal(30, appearance.MetricValue("TitleX"));
        });
    }
}

/// <summary>A Lifetime that dies with the test, so screen subscriptions unwind.</summary>
internal sealed class LifetimeScope : IDisposable
{
    public Lifetime Value { get; } = new();

    public void Dispose() => Value.Destroy();
}
