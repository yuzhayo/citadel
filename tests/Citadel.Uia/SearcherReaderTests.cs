using System.IO;
using Citadel.Searcher;

namespace Citadel.Uia;

/// <summary>
/// `module.json` is identity and is validated strictly; `layout.json` is
/// presentation and is fail-soft. These pin both halves,
/// including supported comments, trailing commas, and case-insensitive fields.
/// </summary>
public class SearcherReaderTests
{
    private static readonly string[] Reserved =
    [
        "settings",
        "settings/appearance",
        "settings/layout",
        "settings/gallery",
    ];

    private const string Valid = """
        {
          "title": "Blank",
          "route": "blank",
          "order": 999,
          "entry": "Module.Blank.dll",
          "type": "Module.Blank.BlankModule"
        }
        """;

    [Fact]
    public void AllSixFieldsSurviveTranslation()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("gateway", ("module.json", """
            {
              "title": "Gateway",
              "icon": "",
              "route": "gateway",
              "order": 10,
              "entry": "Module.Gateway.dll",
              "type": "Module.Gateway.GatewayModule"
            }
            """));

        var manifest = Reader.ReadManifest(folder, Reserved, out var error);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("Gateway", manifest!.Title);
        Assert.Equal("gateway", manifest.Route);
        Assert.Equal("Module.Gateway.GatewayModule", manifest.Type);
        Assert.Equal(10, manifest.Order);
        Assert.Equal("", manifest.Icon);
        Assert.Equal(Path.Combine(folder, "Module.Gateway.dll"), manifest.Entry);
    }

    /// <summary>Required manifest fields are verified rather than assumed.</summary>
    [Fact]
    public void CommentsAndTrailingCommasParse()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("module.json", """
            {
              // the sidebar label
              "title": "Blank",
              /* route must match IModule.Route */
              "route": "blank",
              "entry": "Module.Blank.dll",
              "type": "Module.Blank.BlankModule",
            }
            """));

        var manifest = Reader.ReadManifest(folder, Reserved, out var error);

        Assert.Null(error);
        Assert.Equal("blank", manifest!.Route);
        Assert.Equal(0, manifest.Order);
    }

    [Fact]
    public void PropertyNamesAreCaseInsensitive()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("module.json", """
            {
              "Title": "Blank",
              "ROUTE": "blank",
              "Entry": "Module.Blank.dll",
              "TYPE": "Module.Blank.BlankModule"
            }
            """));

        Assert.NotNull(Reader.ReadManifest(folder, Reserved, out _));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("route")]
    [InlineData("entry")]
    [InlineData("type")]
    public void EachRequiredFieldIsNamedWhenMissing(string field)
    {
        using var root = ModuleFolder.Create();
        var json = Valid.Replace($"\"{field}\"", "\"unused\"", StringComparison.Ordinal);
        var folder = root.Folder("broken", ("module.json", json));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains(field, error);
    }

    [Fact]
    public void BlankRequiredValueIsRefusedLikeAMissingOne()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("broken", ("module.json", Valid.Replace(
            "\"Blank\"", "\"   \"", StringComparison.Ordinal)));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains("title", error);
    }

    [Fact]
    public void MalformedJsonFailsTheFolderRatherThanThrowing()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("broken", ("module.json", "{ \"title\": "));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains("module.json", error);
    }

    [Fact]
    public void ANonObjectManifestIsRefused()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("broken", ("module.json", "[]"));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains("must be a JSON object", error);
    }

    /// <summary>Not sandboxing — one folder must not load another's assembly.</summary>
    [Theory]
    [InlineData("..\\\\other\\\\Module.Other.dll")]
    [InlineData("../other/Module.Other.dll")]
    [InlineData("sub/../../other/Module.Other.dll")]
    public void EntryEscapingTheFolderIsRefused(string entry)
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("module.json", Valid.Replace(
            "Module.Blank.dll", entry, StringComparison.Ordinal)));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains("outside the screen folder", error);
    }

    [Fact]
    public void AnAbsoluteEntryIsRefused()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("module.json", Valid.Replace(
            "Module.Blank.dll",
            "C:\\\\Windows\\\\System32\\\\kernel32.dll",
            StringComparison.Ordinal)));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains("must be relative", error);
    }

    /// <summary>A subfolder is inside the folder, so it is allowed.</summary>
    [Fact]
    public void EntryInASubfolderIsAllowed()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("module.json", Valid.Replace(
            "Module.Blank.dll", "lib/Module.Blank.dll", StringComparison.Ordinal)));

        var manifest = Reader.ReadManifest(folder, Reserved, out var error);

        Assert.Null(error);
        Assert.Equal(Path.Combine(folder, "lib", "Module.Blank.dll"), manifest!.Entry);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("settings/appearance")]
    [InlineData("settings/layout")]
    [InlineData("settings/gallery")]
    public void ReservedRoutesAreRefusedBeforeAnyAssemblyLoads(string route)
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("impostor", ("module.json", Valid.Replace(
            "\"blank\"", $"\"{route}\"", StringComparison.Ordinal)));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains("reserved core route", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("two//slashes")]
    [InlineData("has space")]
    [InlineData("has\\\\backslash")]
    public void AMalformedRouteIsRefused(string route)
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("broken", ("module.json", Valid.Replace(
            "\"blank\"", $"\"{route}\"", StringComparison.Ordinal)));

        Assert.Null(Reader.ReadManifest(folder, Reserved, out var error));
        Assert.Contains("route", error);
    }

    [Fact]
    public void AFolderWithoutAManifestIsNotACitizen()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("notacitizen", ("readme.txt", "just files"));

        Assert.False(Reader.IsCitizenFolder(folder));
    }

    [Fact]
    public void MissingLayoutIsNormalAndSilent()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("module.json", Valid));

        Assert.Null(Reader.ReadLayout(folder, out var warning));
        Assert.Null(warning);
    }

    [Fact]
    public void AValidLayoutBecomesADeclaration()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("layout.json", """
            {
              "slots": {
                "Message":  { "kind": "visibility", "visible": true },
                "PoolList": { "kind": "size", "w": 640, "h": 380 }
              }
            }
            """));

        var declaration = Reader.ReadLayout(folder, out var warning);

        Assert.Null(warning);
        Assert.NotNull(declaration);
        Assert.Equal(["Message", "PoolList"], declaration!.SlotNames);
        Assert.Equal("visibility", declaration.KindOf("Message"));
        Assert.Equal("size", declaration.KindOf("PoolList"));
    }

    /// <summary>
    /// Fail-soft: identity is still valid, so the screen registers with no
    /// editable slots and a warning the caller surfaces.
    /// </summary>
    [Theory]
    [InlineData("{ not json", "could not be read")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{ \"other\": {} }", "no 'slots' object")]
    [InlineData("{ \"slots\": { \"Message\": { \"visible\": true } } }", "without a kind")]
    [InlineData("{ \"slots\": { \"Message\": 7 } }", "without a kind")]
    public void AnUnusableLayoutWarnsWithoutFailingTheFolder(string json, string expected)
    {
        using var root = ModuleFolder.Create();
        var folder = root.Folder("blank", ("module.json", Valid), ("layout.json", json));

        Assert.Null(Reader.ReadLayout(folder, out var warning));
        Assert.Contains(expected, warning);

        // The point of fail-soft: identity is untouched.
        Assert.NotNull(Reader.ReadManifest(folder, Reserved, out _));
    }
}
