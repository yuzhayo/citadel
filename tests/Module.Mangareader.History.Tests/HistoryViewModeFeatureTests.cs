using System.IO;
using System.Text.Json;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History.Tests;

/// <summary>
/// History's Grid/List preference is independent of Library's: it defaults to
/// Grid, autosaves under exactly its own key in its own file, and a Library
/// choice can never move it.
/// </summary>
public sealed class HistoryViewModeFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-history-viewmode-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _historyPath;
    private readonly string _libraryPath;

    public HistoryViewModeFeatureTests()
    {
        Directory.CreateDirectory(_root);
        _historyPath = Path.Combine(_root, "history-view-mode.json");
        _libraryPath = Path.Combine(_root, "library-view-mode.json");
    }

    private HistoryViewModeFeature Feature() => new(
        new ViewModePreferenceStore(HistoryViewModeFeature.PreferenceKey, "history-view-mode.json", _historyPath));

    [Fact]
    public void TheDefaultIsGridAndAChoiceAutosavesUnderOnlyTheHistoryKey()
    {
        var feature = Feature();
        Assert.Equal(MangaViewMode.Grid, feature.Mode);

        feature.Select(MangaViewMode.List);

        var document = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(_historyPath))!;

        var key = Assert.Single(document.Keys);
        Assert.Equal("History.ViewMode", key);
        Assert.Equal("List", document[key]);

        Assert.Equal(MangaViewMode.List, Feature().Restore());
    }

    [Fact]
    public void LibraryViewModeRemainsIndependentInBothDirections()
    {
        // The isolation mechanism is the key plus its own file, so the other
        // feature's preference is exercised through the same shared store
        // without this project linking any Library source.
        var library = new ViewModePreferenceStore(
            "Library.ViewMode",
            "library-view-mode.json",
            _libraryPath);

        Assert.True(library.Save(MangaViewMode.List, out _));
        Assert.Equal(MangaViewMode.Grid, Feature().Mode);

        var history = Feature();
        history.Select(MangaViewMode.List);
        Assert.Equal(MangaViewMode.List, library.Load(out _));

        // Each key reads back its own value and nothing else.
        Assert.Equal(MangaViewMode.List, Feature().Restore());
        Assert.Equal(MangaViewMode.List, library.Load(out _));

        history.Select(MangaViewMode.Grid);
        Assert.Equal(MangaViewMode.List, library.Load(out _));
        Assert.Equal(MangaViewMode.Grid, Feature().Restore());
    }

    [Fact]
    public void ReSelectingTheCurrentModeNeitherRewritesNorNotifies()
    {
        var feature = Feature();
        feature.Select(MangaViewMode.List);
        var written = File.ReadAllText(_historyPath);

        var notifications = 0;
        feature.Changed += (_, _) => notifications++;

        feature.Select(MangaViewMode.List);

        Assert.Equal(0, notifications);
        Assert.Equal(written, File.ReadAllText(_historyPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
