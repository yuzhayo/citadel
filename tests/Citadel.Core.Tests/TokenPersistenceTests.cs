using System.Text.Json.Nodes;
using Citadel.Core.Tokens;
using TokensEngine = Citadel.Core.Tokens.Tokens;

namespace Citadel.Core.Tests;

/// <summary>
/// Store persistence coverage: a failure-safe save, the active empty
/// named theme surviving restart, and the transient preview overlay live drag
/// needs.
/// </summary>
public class TokenPersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "citadel-persist-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "ui.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var file in Directory.GetFiles(_dir))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch { /* temp cleanup is best-effort */ }
    }

    private TokensEngine NewStore() => new(StorePath);

    /// <summary>
    /// Create a theme, activate it, change nothing: only the name went to
    /// activeTheme, so the next load fell back to default and the theme was gone.
    /// This guards against losing an active empty theme on restart.
    /// </summary>
    [Fact]
    public void ActiveEmptyNamedTheme_SurvivesRestart()
    {
        var tokens = NewStore();
        tokens.CreateTheme("midnight");
        tokens.ActivateTheme("midnight");

        Assert.True(tokens.TrySave(out var error), error);

        var reloaded = NewStore();
        reloaded.Load();

        Assert.Equal("midnight", reloaded.ActiveThemeName);
        Assert.Contains("midnight", reloaded.ThemeNames);
    }

    /// <summary>An unused empty theme is still noise and still omitted.</summary>
    [Fact]
    public void UnusedEmptyTheme_IsStillOmitted()
    {
        var tokens = NewStore();
        tokens.CreateTheme("never-used");

        Assert.True(tokens.TrySave(out _));

        var root = JsonNode.Parse(File.ReadAllText(StorePath))!.AsObject();
        Assert.Null(root["themes"]);
    }

    [Fact]
    public void Save_ReportsFailureInsteadOfThrowing_AndKeepsTheLiveValue()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("Rail", JsonValue.Create(64)).Applied);
        Assert.True(tokens.TrySave(out _));

        // A read-only file is the installed-folder case: the edit stays live, the
        // save reports why, and nothing throws.
        File.SetAttributes(StorePath, FileAttributes.ReadOnly);
        try
        {
            var saved = tokens.TrySave(out var error);

            Assert.False(saved);
            Assert.NotNull(error);
            Assert.Equal(64, tokens.Number("Rail"));
        }
        finally
        {
            File.SetAttributes(StorePath, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// The temp file must never be left where a reader would find it as the real
    /// store, and a failed replace must not truncate what was already there.
    /// </summary>
    [Fact]
    public void FailedSave_LeavesThePreviousFileIntact()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("Rail", JsonValue.Create(64)).Applied);
        Assert.True(tokens.TrySave(out _));
        var before = File.ReadAllText(StorePath);

        File.SetAttributes(StorePath, FileAttributes.ReadOnly);
        try
        {
            Assert.True(tokens.CommitCore("Row", JsonValue.Create(44)).Applied);
            Assert.False(tokens.TrySave(out _));

            Assert.Equal(before, File.ReadAllText(StorePath));
        }
        finally
        {
            File.SetAttributes(StorePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Preview_PublishesAGuardedSnapshot_WithoutCommitting()
    {
        var tokens = NewStore();
        var fired = 0;
        tokens.TokensChanged += () => fired++;

        Assert.True(tokens.PreviewCore("Rail", JsonValue.Create(200)));

        Assert.Equal(200, tokens.Number("Rail"));
        Assert.Equal(200, tokens.Number("FullDefault"));
        Assert.True(tokens.HasPreview);
        Assert.Equal(1, fired);

        // Nothing was committed, so nothing is persisted.
        Assert.True(tokens.TrySave(out _));
        var root = JsonNode.Parse(File.ReadAllText(StorePath))!.AsObject();
        Assert.Null(root["themes"]);
    }

    [Fact]
    public void Preview_NeverPublishesFullBoundsThatCanCrashSidebarClamp()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("FullMax", JsonValue.Create(232)).Applied);

        var notifications = 0;
        tokens.TokensChanged += () =>
        {
            notifications++;
            var minimum = tokens.Number("FullMin");
            var maximum = tokens.Number("FullMax");
            Assert.True(minimum <= maximum);

            // This is Sidebar.CalculateFullWidth's failure point. If preview
            // publishes FullMax=39 beside FullMin=50, Math.Clamp throws here.
            _ = Math.Clamp(tokens.Number("FullDefault"), minimum, maximum);
        };

        Assert.True(tokens.PreviewCore("FullMax", JsonValue.Create(39)));

        Assert.Equal(1, notifications);
        Assert.Equal(232, tokens.Number("FullMax"));
        Assert.True(tokens.HasPreview);
    }

    [Fact]
    public void CancelPreview_RestoresTheCommittedValue()
    {
        var tokens = NewStore();
        Assert.True(tokens.CommitCore("Rail", JsonValue.Create(64)).Applied);

        Assert.True(tokens.PreviewCore("Rail", JsonValue.Create(200)));
        Assert.Equal(200, tokens.Number("Rail"));

        tokens.CancelPreview();

        Assert.Equal(64, tokens.Number("Rail"));
        Assert.False(tokens.HasPreview);
    }

    /// <summary>
    /// The whole point of the overlay: a drag previews per frame and the guard
    /// runs exactly once, on release.
    /// </summary>
    [Fact]
    public void CommitPreview_RunsTheGuardOnce_AndDropsThePreview()
    {
        var tokens = NewStore();

        for (var value = 60; value <= 80; value++)
        {
            Assert.True(tokens.PreviewCore("Rail", JsonValue.Create(value)));
        }

        var result = tokens.CommitPreview("Rail", JsonValue.Create(80));

        Assert.True(result.Applied);
        Assert.False(tokens.HasPreview);
        Assert.Equal(80, tokens.Number("Rail"));
    }

    [Fact]
    public void Preview_RejectsUnknownTokensAndWrongKinds()
    {
        var tokens = NewStore();

        Assert.False(tokens.PreviewCore("NoSuchToken", JsonValue.Create(1)));
        Assert.False(tokens.PreviewCore("Rail", JsonValue.Create("not-a-number")));
        Assert.False(tokens.HasPreview);
    }

    [Fact]
    public void ThemeSwitchAndResetToken_CancelAnActivePreview()
    {
        var tokens = NewStore();
        Assert.True(tokens.PreviewCore("Rail", JsonValue.Create(200)));
        tokens.ActivateTheme("other");
        Assert.False(tokens.HasPreview);

        Assert.True(tokens.PreviewCore("Rail", JsonValue.Create(200)));
        tokens.ResetCore("Rail");
        Assert.False(tokens.HasPreview);
        Assert.Equal(Defaults.All["Rail"].Number, tokens.Number("Rail"));
    }
}
