using System.Text.Json.Nodes;
using Citadel.Core.Tokens;
using TokensEngine = Citadel.Core.Tokens.Tokens;

namespace Citadel.Core.Tests;

/// <summary>
/// The sidebar geometry invariant: expanding must never make the
/// sidebar narrower than its collapsed rail.
///
/// The existing FullMin/FullMax/FullDefault checks did not imply it. FullMin=50 is
/// below Rail=58 on purpose, so the rail is FullDefault's
/// effective floor rather than something FullMin is forced up to.
/// </summary>
public class GuardGeometryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "citadel-geometry-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "ui.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp cleanup is best-effort */ }
    }

    private TokensEngine NewStore() => new(StorePath);

    /// <summary>
    /// The reported case: FullDefault=50 was accepted with zero issues, leaving
    /// the expanded sidebar narrower than the 58px rail it collapses to.
    /// </summary>
    [Fact]
    public void FullDefaultBelowRail_ClampsToRail_AndLeavesFullMinAlone()
    {
        var tokens = NewStore();

        var result = tokens.CommitCore("FullDefault", JsonValue.Create(50));

        Assert.Contains(
            result.Issues,
            issue => issue.Token == "FullDefault" && issue.Verdict == GuardVerdict.Clamped);
        Assert.Equal(tokens.Number("Rail"), tokens.Number("FullDefault"));
        Assert.Equal(50, tokens.Number("FullMin"));
    }

    /// <summary>
    /// The conclusive case: a rail wider than FullMax means the clamped expanded
    /// width is *below* the collapsed width no matter what FullDefault says.
    /// Both participants are reverted, because either one could be the typo.
    /// </summary>
    [Fact]
    public void RailWiderThanFullMax_CannotSurviveTheGuard()
    {
        var core = new Dictionary<string, TokenValue>(StringComparer.Ordinal)
        {
            ["Rail"] = TokenValue.OfNumber(321),
            ["FullMax"] = TokenValue.OfNumber(320),
        };

        var issues = Guard.Check(core);

        Assert.Contains(issues, issue => issue.Token == "Rail" && issue.Verdict == GuardVerdict.Clamped);
        Assert.Contains(issues, issue => issue.Token == "FullMax" && issue.Verdict == GuardVerdict.Clamped);
        Assert.False(core.ContainsKey("Rail"));
        Assert.False(core.ContainsKey("FullMax"));
    }

    /// <summary>
    /// A repair later in the pass must not invalidate bounds checked earlier.
    /// FullMax is valid at 400 until Rail=480 reverts it to the 320 default;
    /// that makes the existing FullMin=350 invalid and used to reach
    /// Math.Clamp(350, 320), which throws.
    /// </summary>
    [Fact]
    public void RepairingRail_ReconcilesFullBoundsAgain()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("FullMax", JsonValue.Create(400)).Applied);
        Assert.True(tokens.CommitCore("FullMin", JsonValue.Create(350)).Applied);

        var result = tokens.CommitCore("Rail", JsonValue.Create(480));

        Assert.False(result.Applied);
        AssertConsistent(tokens);
        Assert.Equal(Defaults.All["FullMin"].Number, tokens.Number("FullMin"));
        Assert.Contains(result.Issues, issue =>
            issue.Token == "FullMin" && issue.Verdict == GuardVerdict.Clamped);
    }

    /// <summary>
    /// Reverting Rail can leave TitleX beyond the restored rail — and the TitleX
    /// check already ran earlier in the same pass. Repairing one invariant must
    /// not break another.
    /// </summary>
    [Fact]
    public void RepairingTheRail_ReappliesTheTitleOffsetInvariant()
    {
        var core = new Dictionary<string, TokenValue>(StringComparer.Ordinal)
        {
            ["Rail"] = TokenValue.OfNumber(400),
            ["TitleX"] = TokenValue.OfNumber(390),
            ["FullMax"] = TokenValue.OfNumber(320),
        };

        Guard.Check(core);

        var rail = core.TryGetValue("Rail", out var r) ? r.Number : Defaults.All["Rail"].Number;
        var titleX = core.TryGetValue("TitleX", out var t) ? t.Number : Defaults.All["TitleX"].Number;
        Assert.True(titleX <= rail, $"TitleX={titleX} Rail={rail}");
    }

    /// <summary>
    /// The postcondition itself, over the guard-permitted extremes rather than a
    /// handful of spot values — the shape that let two stage-2 defects through.
    /// </summary>
    [Theory]
    [InlineData("Rail", 24)]
    [InlineData("Rail", 300)]
    [InlineData("Rail", 400)]
    [InlineData("FullMin", 1)]
    [InlineData("FullMin", 300)]
    [InlineData("FullMax", 60)]
    [InlineData("FullMax", 480)]
    [InlineData("FullDefault", 1)]
    [InlineData("FullDefault", 480)]
    public void AnyPermittedEdit_LeavesTheGeometryConsistent(string token, double value)
    {
        var tokens = NewStore();

        tokens.CommitCore(token, JsonValue.Create(value));

        AssertConsistent(tokens);
    }

    [Fact]
    public void LoadedOverrideSet_SatisfiesTheInvariant()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            { "activeTheme": "default",
              "themes": { "default": { "core": {
                "Rail": 321, "FullMax": 320, "FullDefault": 60 } } } }
            """);

        var tokens = NewStore();
        var issues = tokens.Load();

        Assert.NotEmpty(issues);
        AssertConsistent(tokens);
    }

    [Fact]
    public void ActivatedTheme_SatisfiesTheInvariant()
    {
        var tokens = NewStore();
        tokens.CreateTheme("narrow");
        tokens.ActivateTheme("narrow");
        tokens.CommitCore("Rail", JsonValue.Create(120));
        tokens.ActivateTheme(TokensEngine.DefaultThemeName);

        var issues = tokens.ActivateTheme("narrow");

        AssertConsistent(tokens);
        Assert.NotNull(issues);
    }

    private static void AssertConsistent(TokensEngine tokens)
    {
        var rail = tokens.Number("Rail");
        var min = tokens.Number("FullMin");
        var max = tokens.Number("FullMax");
        var full = tokens.Number("FullDefault");

        Assert.True(max >= rail, $"FullMax={max} must be at least Rail={rail}");
        Assert.True(min <= max, $"FullMin={min} must not exceed FullMax={max}");
        Assert.True(
            full >= Math.Max(min, rail),
            $"FullDefault={full} must be at least max(FullMin={min}, Rail={rail})");
        Assert.True(full <= max, $"FullDefault={full} must not exceed FullMax={max}");
        Assert.True(
            tokens.Number("TitleX") <= rail,
            $"TitleX={tokens.Number("TitleX")} must not exceed Rail={rail}");
    }
}
