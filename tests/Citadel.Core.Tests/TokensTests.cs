using System.Text.Json.Nodes;
using Citadel.Core;
using Citadel.Core.Tokens;
using TokensEngine = Citadel.Core.Tokens.Tokens;

namespace Citadel.Core.Tests;

/// <summary>
/// These tests pin the token store behavior: defaults in code, sparse
/// overrides, override ?? default, guard verdicts, round-trip.
/// </summary>
public class TokensTests : IDisposable
{
    private readonly string _dir =
        System.IO.Path.Combine(Path.GetTempPath(), "citadel-tokens-" + Guid.NewGuid().ToString("N"));

    private string StorePath => System.IO.Path.Combine(_dir, "ui.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp cleanup is best-effort */ }
    }

    private TokensEngine NewStore() => new(StorePath);

    [Fact]
    public void Defaults_InCode_EveryTokenAlwaysResolves()
    {
        var tokens = NewStore();

        Assert.Equal(58, tokens.Number("Rail"));
        Assert.Equal(192, tokens.Number("FullDefault"));
        Assert.Equal(0xFF121211u, tokens.Color("BgRail"));
        Assert.Equal(0xFFC9A96Au, tokens.Color("Accent"));
        Assert.Equal(0xFF2A2926u, tokens.Color("Border"));
        Assert.Equal(0xFF1E1D1Bu, tokens.Color("Card"));
        Assert.Equal(0xFFB5B3AEu, tokens.Color("Body"));

        foreach (var name in Defaults.All.Keys)
        {
            _ = tokens.Resolve(name); // no partial state
        }

        Assert.Throws<ArgumentException>(() => tokens.Resolve("NoSuchToken"));
    }

    [Fact]
    public void Commit_OverrideWins_AndFiresTheSignal()
    {
        var tokens = NewStore();
        var fired = 0;
        tokens.TokensChanged += () => fired++;

        var result = tokens.CommitCore("BgRail", JsonValue.Create("#0E0E0D"));

        Assert.True(result.Applied);
        Assert.Equal(0xFF0E0E0Du, tokens.Color("BgRail"));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Commit_InvalidOrUnknown_IsIgnoredAndLogged()
    {
        var tokens = NewStore();

        var before = tokens.Number("Rail");
        var invalid = tokens.CommitCore("Rail", JsonValue.Create("not-a-number"));
        var unknown = tokens.CommitCore("NoSuchToken", JsonValue.Create(1));
        var wrongKind = tokens.CommitCore("BgRail", JsonValue.Create(42));

        Assert.False(invalid.Applied);
        Assert.False(unknown.Applied);
        Assert.False(wrongKind.Applied);
        Assert.NotNull(invalid.RejectionReason);
        Assert.NotNull(unknown.RejectionReason);
        Assert.NotNull(wrongKind.RejectionReason);
        Assert.Equal(before, tokens.Number("Rail"));
        Assert.Equal(0xFF121211u, tokens.Color("BgRail"));
    }

    [Fact]
    public void Guard_NonPositiveSize_ClampsToDefault()
    {
        var tokens = NewStore();

        var result = tokens.CommitCore("Row", JsonValue.Create(0));

        Assert.False(result.Applied);
        Assert.Contains(result.Issues, i => i.Verdict == GuardVerdict.Clamped);
        Assert.Equal(40, tokens.Number("Row")); // default restored
    }

    [Fact]
    public void Guard_TitleOffset_ClampsToRail()
    {
        var tokens = NewStore();

        var result = tokens.CommitCore("TitleX", JsonValue.Create(999));

        Assert.True(result.Applied);
        Assert.Contains(result.Issues, i => i.Token == "TitleX" && i.Verdict == GuardVerdict.Clamped);
        Assert.Equal(58, tokens.Number("TitleX")); // the rail width
    }

    [Fact]
    public void Guard_TransparentForeground_IsRefused()
    {
        var tokens = NewStore();

        var result = tokens.CommitCore("Fg", JsonValue.Create("#00000000"));

        Assert.False(result.Applied);
        Assert.Contains(result.Issues, i => i.Token == "Fg" && i.Verdict == GuardVerdict.Refused);
        Assert.Equal(0xFFD6D6D3u, tokens.Color("Fg")); // untouched
    }

    [Fact]
    public void Guard_ContrastBelowThreshold_WarnsButAllows()
    {
        var tokens = NewStore();

        var result = tokens.CommitCore("Fg", JsonValue.Create("#171716")); // == Bg: ratio 1.0

        Assert.True(result.Applied);
        Assert.Contains(result.Issues, i => i.Verdict == GuardVerdict.Warned);
        Assert.Equal(0xFF171716u, tokens.Color("Fg")); // allowed
    }

    /// <summary>
    /// The invariant is about resolved values, not about which keys happen to
    /// be in the override set. Narrowing the rail is the natural edit, and it
    /// leaves TitleX at its default — outside the rail it is drawn in.
    /// </summary>
    [Fact]
    public void Guard_NarrowingTheRail_ClampsATitleXThatWasNeverOverridden()
    {
        var tokens = NewStore();

        var result = tokens.CommitCore("Rail", JsonValue.Create(30));

        Assert.True(result.Applied);
        Assert.Contains(result.Issues, i => i.Token == "TitleX" && i.Verdict == GuardVerdict.Clamped);
        Assert.Equal(30, tokens.Number("TitleX"));
    }

    [Theory]
    [InlineData("FullMin", 300, 300)] // raising the floor pulls the width up
    [InlineData("FullMax", 180, 180)] // lowering the ceiling pushes it down
    public void Guard_MovingOneBound_ClampsAFullDefaultThatWasNeverOverridden(
        string bound, int value, double expected)
    {
        var tokens = NewStore();

        var result = tokens.CommitCore(bound, JsonValue.Create(value));

        Assert.True(result.Applied);
        Assert.Contains(result.Issues, i => i.Token == "FullDefault" && i.Verdict == GuardVerdict.Clamped);
        Assert.Equal(expected, tokens.Number("FullDefault"));
        Assert.InRange(tokens.Number("FullDefault"), tokens.Number("FullMin"), tokens.Number("FullMax"));
    }

    [Theory]
    [InlineData("WindowMinW", 2000, "WindowW")]
    [InlineData("WindowMinH", 1200, "WindowH")]
    public void Guard_RaisingAWindowMinimum_PullsTheInitialSizeUp(
        string minimumToken, int minimum, string initialToken)
    {
        var tokens = NewStore();

        var result = tokens.CommitCore(minimumToken, JsonValue.Create(minimum));

        Assert.True(result.Applied);
        Assert.Contains(result.Issues,
            issue => issue.Token == initialToken && issue.Verdict == GuardVerdict.Clamped);
        Assert.Equal(minimum, tokens.Number(minimumToken));
        Assert.Equal(minimum, tokens.Number(initialToken));
    }

    [Fact]
    public void Load_WithOneBoundOverridden_StillSatisfiesTheInvariant()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            { "activeTheme": "default",
              "themes": { "default": { "core": { "FullMin": 300 } } } }
            """);

        var tokens = NewStore();
        var issues = tokens.Load();

        Assert.Contains(issues, i => i.Token == "FullDefault" && i.Verdict == GuardVerdict.Clamped);
        Assert.InRange(tokens.Number("FullDefault"), tokens.Number("FullMin"), tokens.Number("FullMax"));
    }

    /// <summary>
    /// "Rejected" has to mean nothing changed. Dropping the offending key fell
    /// back to the default, silently discarding the good value the user had
    /// there while the result still reported the edit as rejected.
    /// </summary>
    [Fact]
    public void Commit_Rejected_KeepsThePreviousGoodValue()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("Fg", JsonValue.Create("#FF0000")).Applied);
        Assert.True(tokens.CommitCore("Row", JsonValue.Create(48)).Applied);

        var refused = tokens.CommitCore("Fg", JsonValue.Create("#00000000"));
        var clamped = tokens.CommitCore("Row", JsonValue.Create(0));

        Assert.False(refused.Applied);
        Assert.False(clamped.Applied);
        Assert.Equal(0xFFFF0000u, tokens.Color("Fg"));
        Assert.Equal(48, tokens.Number("Row"));
        Assert.NotNull(refused.RejectionReason);
        Assert.Contains("kept previous", refused.RejectionReason);
    }

    [Fact]
    public void ResetToken_DeletesTheKey_FallsBackToDefault()
    {
        var tokens = NewStore();

        tokens.CommitCore("Rail", JsonValue.Create(64));
        tokens.ResetCore("Rail");

        Assert.Equal(58, tokens.Number("Rail"));
    }

    [Fact]
    public void Commit_DefaultValue_RemovesTheOverrideBeforeSaving()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("Rail", JsonValue.Create(64)).Applied);

        var resetToDefault = tokens.CommitCore("Rail", JsonValue.Create(58));
        tokens.Save();

        Assert.True(resetToDefault.Applied);
        Assert.Equal(58, tokens.Number("Rail"));
        var root = JsonNode.Parse(File.ReadAllText(StorePath))!.AsObject();
        Assert.Null(root["themes"]);
    }

    [Fact]
    public void SparseWrite_RoundTrips()
    {
        var tokens = NewStore();
        tokens.CommitCore("BgRail", JsonValue.Create("#0E0E0D"));
        tokens.CommitCore("Rail", JsonValue.Create(64));
        Assert.True(tokens.CommitLayout("gateway", "poolTable", "w", JsonValue.Create(720)));
        tokens.Save();

        var reloaded = NewStore();
        reloaded.Load();

        Assert.Equal(0xFF0E0E0Du, reloaded.Color("BgRail"));
        Assert.Equal(64, reloaded.Number("Rail"));
        Assert.Equal(40, reloaded.Number("Row")); // untouched stays default
        Assert.Equal(720d,
            reloaded.LayoutOverrides("gateway")!["poolTable"]!["w"]!.GetValue<double>());
    }

    [Fact]
    public void CorruptFile_LeavesPristineDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ this is not json");

        var tokens = NewStore();
        var exception = Record.Exception(tokens.Load);

        Assert.Null(exception);
        Assert.Equal(58, tokens.Number("Rail"));
    }

    [Fact]
    public void StructurallyInvalidFile_ReloadsAnExistingStoreToPristineDefaults()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("Rail", JsonValue.Create(64)).Applied);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "[]");

        var exception = Record.Exception(tokens.Load);

        Assert.Null(exception);
        Assert.Equal(58, tokens.Number("Rail"));
    }

    [Fact]
    public void EmptyFile_EveryTokenResolvesToItsDefault()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{}");

        var tokens = NewStore();
        var issues = tokens.Load();

        Assert.Empty(issues);
        foreach (var (token, value) in Defaults.All)
        {
            Assert.Equal(value, tokens.Resolve(token));
        }
    }

    [Fact]
    public void PartialFile_OverridesOnlyItsDeclaredToken()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            {
              "activeTheme": "default",
              "themes": {
                "default": {
                  "core": { "BgRail": "#0E0E0D" }
                }
              }
            }
            """);

        var tokens = NewStore();
        var issues = tokens.Load();

        Assert.Empty(issues);
        Assert.Equal(0xFF0E0E0Du, tokens.Color("BgRail"));
        foreach (var (token, value) in Defaults.All.Where(pair => pair.Key != "BgRail"))
        {
            Assert.Equal(value, tokens.Resolve(token));
        }
    }

    [Fact]
    public void SemanticCorruptFile_RevertsContradictoryFullBoundsWithoutCrashing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            {
              "activeTheme": "default",
              "themes": {
                "default": {
                  "core": {
                    "FullMin": 400,
                    "FullMax": 300,
                    "FullDefault": 350
                  }
                }
              }
            }
            """);

        var tokens = NewStore();
        IReadOnlyList<GuardIssue>? issues = null;
        var exception = Record.Exception(() => issues = tokens.Load());

        Assert.Null(exception);
        Assert.NotNull(issues);
        Assert.Contains(issues, issue => issue.Token == "FullMin" && issue.Verdict == GuardVerdict.Clamped);
        Assert.Contains(issues, issue => issue.Token == "FullMax" && issue.Verdict == GuardVerdict.Clamped);
        Assert.Contains("load sanitized theme 'default', token 'FullMin'", Log.Full());
        Assert.Equal(50, tokens.Number("FullMin"));
        Assert.Equal(320, tokens.Number("FullMax"));
        Assert.Equal(320, tokens.Number("FullDefault"));
    }

    [Fact]
    public void Defaults_EveryPairAndGeometryInvariantPass()
    {
        foreach (var (foreground, background) in TokenPairs.Contrast)
        {
            var ratio = Guard.ContrastRatio(Defaults.All[foreground], Defaults.All[background]);
            Assert.True(ratio >= TokenPairs.MinContrast,
                $"{foreground} on {background} has contrast {ratio:F2}");
        }

        foreach (var value in Defaults.All.Values.Where(value => value.Kind == TokenKind.Number))
        {
            Assert.True(value.Number > 0);
        }
        Assert.True(Defaults.All["TitleX"].Number <= Defaults.All["Rail"].Number);
        Assert.InRange(Defaults.All["FullDefault"].Number,
            Defaults.All["FullMin"].Number, Defaults.All["FullMax"].Number);
        Assert.True(Defaults.All["WindowMinW"].Number <= Defaults.All["WindowW"].Number);
        Assert.True(Defaults.All["WindowMinH"].Number <= Defaults.All["WindowH"].Number);
        Assert.Empty(Guard.Check([]));
    }

    [Fact]
    public void DefaultGridMetrics_KeepTheirIntentionalStartingValues()
    {
        Assert.Equal(40, Defaults.All["Row"].Number);
        Assert.Equal(49, Defaults.All["TitleX"].Number);
    }

    [Fact]
    public void ContrastRatio_UsesTheWcagLinearChannelThreshold()
    {
        Assert.True(TokenValue.TryParseColor("#0A0000", out var nearBlackRed));
        Assert.True(TokenValue.TryParseColor("#000000", out var black));

        var expected = (0.2126 * ((10d / 255d) / 12.92) + 0.05) / 0.05;

        Assert.Equal(expected, Guard.ContrastRatio(nearBlackRed, black), precision: 7);
    }

    [Fact]
    public void CommitLayout_RejectsUndeclaredPropertyWithoutCreatingAnOverride()
    {
        var tokens = NewStore();

        var applied = tokens.CommitLayout("gateway", "poolTable", "fontFamily", JsonValue.Create("Inter"));

        Assert.False(applied);
        Assert.Null(tokens.LayoutOverrides("gateway"));
    }

    [Fact]
    public void Load_SanitizesLayoutPropertiesAndValueTypes()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            {
              "activeTheme": "default",
              "themes": {
                "default": {
                  "module": {
                    "gateway": {
                      "poolTable": { "x": 12, "fontFamily": "Inter" },
                      "heartbeat": { "visible": "maybe" }
                    }
                  }
                }
              }
            }
            """);

        var tokens = NewStore();
        tokens.Load();

        var layout = tokens.LayoutOverrides("gateway")!;
        Assert.Equal(12, layout["poolTable"]!["x"]!.GetValue<int>());
        Assert.Null(layout["poolTable"]!["fontFamily"]);
        Assert.Null(layout["heartbeat"]);
    }

    [Fact]
    public void LayoutOverrides_AreDeepClonedAtTheReadBoundary()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitLayout("gateway", "poolTable", "x", JsonValue.Create(10)));

        var editableCopy = tokens.LayoutOverrides("gateway")!;
        editableCopy["poolTable"]!["x"] = 99;

        Assert.Equal(10, tokens.LayoutOverrides("gateway")!["poolTable"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public void Save_SkipsEmptyThemes()
    {
        var tokens = NewStore();
        tokens.CreateTheme("unused");

        tokens.Save();

        var root = JsonNode.Parse(File.ReadAllText(StorePath))!.AsObject();
        Assert.Null(root["themes"]);
    }

    [Fact]
    public void Themes_AreSparseOverrideSets_ScopedPerTheme()
    {
        var tokens = NewStore();

        tokens.CreateTheme("midnight");
        tokens.ActivateTheme("midnight");
        tokens.CommitCore("BgRail", JsonValue.Create("#0E0E0D"));

        tokens.ActivateTheme(TokensEngine.DefaultThemeName);

        Assert.Equal(0xFF121211u, tokens.Color("BgRail")); // default: override stayed in midnight
        tokens.ActivateTheme("midnight");
        Assert.Equal(0xFF0E0E0Du, tokens.Color("BgRail"));
    }

    [Fact]
    public void ActivateTheme_LogsWhenItCreatesAnUnknownTheme()
    {
        var tokens = NewStore();
        var name = "typo-" + Guid.NewGuid().ToString("N");

        tokens.ActivateTheme(name);

        Assert.Equal(name, tokens.ActiveThemeName);
        Assert.Contains($"unknown theme '{name}'", Log.Full());
    }

    [Fact]
    public void ResetAll_DeletesTheFile_AndRestoresDefaults()
    {
        var tokens = NewStore();
        tokens.CommitCore("Rail", JsonValue.Create(64));
        tokens.Save();
        Assert.True(File.Exists(StorePath));

        tokens.ResetAll();

        Assert.False(File.Exists(StorePath));
        Assert.Equal(58, tokens.Number("Rail"));
    }
}
