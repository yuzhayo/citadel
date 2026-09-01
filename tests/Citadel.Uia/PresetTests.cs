using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Citadel.Setting.Components;

namespace Citadel.Uia;

/// <summary>
/// The component library's two hard rules, checked mechanically rather than
/// trusted: a preset never names a route, and a missing or ambiguous id falls
/// back to the plain control.
/// </summary>
public class PresetTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "citadel-preset-" + Guid.NewGuid().ToString("N"));

    public PresetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp cleanup is best-effort */ }
    }

    private void WritePresets(string control, string json) =>
        File.WriteAllText(PresetStore.PathFor(control, _dir), json);

    /// <summary>
    /// A preset must never name a route. The loader checks this instead of
    /// relying on convention.
    /// </summary>
    [Fact]
    public void PresetNamingARoute_IsRefused()
    {
        WritePresets("Field", """
            { "control": "Field", "presets": [
              { "id": "sneaky", "values": { "route": "gateway", "placeholder": "x" } },
              { "id": "clean", "values": { "placeholder": "ok" } } ] }
            """);

        var store = PresetStore.Load("Field", _dir);

        Assert.False(store.Contains("sneaky"));
        Assert.True(store.Contains("clean"));
    }

    [Fact]
    public void DuplicateId_RefusesBoth_RatherThanPickingOne()
    {
        WritePresets("Button", """
            { "control": "Button", "presets": [
              { "id": "twin", "values": { "label": "First" } },
              { "id": "twin", "values": { "label": "Second" } } ] }
            """);

        var store = PresetStore.Load("Button", _dir);

        Assert.False(store.Contains("twin"));
        Assert.Empty(store.All);
    }

    [Fact]
    public void DuplicateId_RemainsRefusedWhenAThirdEntryUsesIt()
    {
        WritePresets("Button", """
            { "control": "Button", "presets": [
              { "id": "twin", "values": { "label": "First" } },
              { "id": "twin", "values": { "label": "Second" } },
              { "id": "twin", "values": { "label": "Third" } } ] }
            """);

        var store = PresetStore.Load("Button", _dir);

        Assert.False(store.Contains("twin"));
        Assert.Empty(store.All);
    }

    [Fact]
    public void MissingFile_LeavesThePlainControlUsable()
    {
        Sta.Run(() =>
        {
            var store = PresetStore.Load("Toggle", _dir);
            var toggle = new SettingToggle();

            Assert.False(PresetApplier.Apply(toggle, store, "anything"));
            Assert.Null(toggle.Content);
        });
    }

    [Fact]
    public void MalformedFile_LeavesThePlainControlUsable()
    {
        Sta.Run(() =>
        {
            WritePresets("Slider", "{ this is not json");

            var store = PresetStore.Load("Slider", _dir);

            Assert.Empty(store.All);
            Assert.False(PresetApplier.Apply(new SettingSlider(), store, "metric-px"));
        });
    }

    [Fact]
    public void SetupWithoutAnId_IsSkipped()
    {
        WritePresets("Field", """
            { "control": "Field", "presets": [
              { "values": { "placeholder": "nameless" } },
              { "id": "named", "values": { "placeholder": "ok" } } ] }
            """);

        var store = PresetStore.Load("Field", _dir);

        Assert.Single(store.All);
        Assert.True(store.Contains("named"));
    }

    /// <summary>Every Gallery-editable primitive has both sides of its preset pair.</summary>
    [Theory]
    [InlineData("Button")]
    [InlineData("Field")]
    [InlineData("Toggle")]
    [InlineData("Slider")]
    [InlineData("Table")]
    public void EveryGalleryPrimitive_HasItsPairedPresetsFile(string control)
    {
        var path = PresetStore.PathFor(control);

        Assert.True(File.Exists(path), $"{control} is missing {path}");

        var store = PresetStore.Load(control);
        Assert.NotEmpty(store.All);
        Assert.All(store.All, preset => Assert.False(string.IsNullOrWhiteSpace(preset.Id)));

        var components = System.IO.Path.Combine(
            RepositoryRoot(), "setting", "Components");
        Assert.True(File.Exists(System.IO.Path.Combine(components, $"{control}.xaml")));
        Assert.True(File.Exists(System.IO.Path.Combine(components, $"{control}.xaml.cs")));
    }

    [Theory]
    [InlineData("ActionCard")]
    [InlineData("PasswordField")]
    [InlineData("TableActions")]
    [InlineData("Tabs")]
    public void FixedBehaviorComponent_DoesNotAdvertiseAnEditablePreset(string control)
    {
        var components = System.IO.Path.Combine(
            RepositoryRoot(), "setting", "Components");

        Assert.False(File.Exists(System.IO.Path.Combine(components, $"{control}.presets.json")));
        Assert.True(File.Exists(System.IO.Path.Combine(components, $"{control}.xaml")));
        Assert.True(File.Exists(System.IO.Path.Combine(components, $"{control}.xaml.cs")));
    }

    [Fact]
    public void SharedDialog_LoadsItsWindowStyleWithoutAParseFailure()
    {
        Sta.Run(() =>
        {
            var dialog = new SettingDialog();

            Assert.NotNull(dialog.Style);
            Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
        });
    }

    /// <summary>And none of the shipped ones may name a route.</summary>
    [Fact]
    public void NoShippedPresetNamesARoute()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Components");
        foreach (var file in Directory.GetFiles(directory, "*.presets.json"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("\"route\"", text);
        }
    }

    [Fact]
    public void TableSortsWhenTold_AndNeverKnowsItsScreen()
    {
        Sta.Run(() =>
        {
            var store = PresetStore.Load("Table");
            var table = new SettingTable();
            table.SetRows([["b", "2"], ["a", "1"], ["c", "3"]]);

            Assert.True(PresetApplier.Apply(table, store, "balance-table"));

            Assert.Equal(["Account", "Currency", "Balance"], table.Columns);
            Assert.True(table.SortDescending);

            table.SortColumn = "Account";
            Assert.Equal(["c", "b", "a"], table.Rows.Select(row => row[0]));

            table.SortDescending = false;
            Assert.Equal(["a", "b", "c"], table.Rows.Select(row => row[0]));
        });
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(System.IO.Path.Combine(directory.FullName, "Citadel.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void SavedPreset_IsReadBackByAFreshStore()
    {
        var store = PresetStore.Load("Field", _dir);
        Assert.True(store.Set(new Preset("wide", "Wide", new JsonObject { ["width"] = 320 })));
        Assert.True(store.TrySave(out var error), error);

        var reloaded = PresetStore.Load("Field", _dir);

        Assert.True(reloaded.Contains("wide"));
        Assert.Equal(320, reloaded.Resolve("wide")!["width"]!.GetValue<double>());
    }

    [Fact]
    public void SaveFailure_IsReportedRatherThanThrown()
    {
        var store = PresetStore.Load("Field", _dir);
        Assert.True(store.Set(new Preset("wide", "Wide", new JsonObject { ["width"] = 320 })));
        Assert.True(store.TrySave(out _));

        File.SetAttributes(store.Path, FileAttributes.ReadOnly);
        try
        {
            Assert.True(store.Set(new Preset("narrow", "Narrow", new JsonObject { ["width"] = 80 })));

            Assert.False(store.TrySave(out var error));
            Assert.NotNull(error);
            Assert.True(store.Contains("narrow")); // live, just not saved
        }
        finally
        {
            File.SetAttributes(store.Path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void SetRefusesAValuesObjectNamingARoute()
    {
        var store = PresetStore.Load("Button", _dir);

        Assert.False(store.Set(new Preset(
            "bad",
            "Bad",
            new JsonObject { ["route"] = "gateway" })));
        Assert.Empty(store.All);
    }
}
