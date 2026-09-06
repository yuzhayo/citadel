using System.IO;
using System.Text.Json;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library.Tests;

/// <summary>
/// Library's Grid/List preference: Grid by default, autosaved under exactly its
/// own key, and structurally incapable of touching another feature's view mode.
/// </summary>
public sealed class LibraryViewModeFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-viewmode-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _libraryPath;
    private readonly string _historyPath;

    public LibraryViewModeFeatureTests()
    {
        Directory.CreateDirectory(_root);
        _libraryPath = Path.Combine(_root, "library-view-mode.json");
        _historyPath = Path.Combine(_root, "history-view-mode.json");
    }

    private LibraryViewModeFeature Feature() => new(
        new ViewModePreferenceStore(LibraryViewModeFeature.PreferenceKey, "library-view-mode.json", _libraryPath));

    [Fact]
    public void TheDefaultIsGridAndAChoiceAutosavesUnderOnlyTheLibraryKey()
    {
        var feature = Feature();
        Assert.Equal(MangaViewMode.Grid, feature.Mode);

        feature.Select(MangaViewMode.List);

        Assert.True(File.Exists(_libraryPath));
        var document = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(_libraryPath))!;

        // Exactly one key: choosing a presentation persists nothing else.
        var key = Assert.Single(document.Keys);
        Assert.Equal("Library.ViewMode", key);
        Assert.Equal("List", document[key]);

        Assert.Equal(MangaViewMode.List, Feature().Restore());
    }

    [Fact]
    public void AnotherFeaturesViewModeFileIsNeverWritten()
    {
        var feature = Feature();
        feature.Select(MangaViewMode.List);
        feature.Select(MangaViewMode.Grid);

        Assert.False(File.Exists(_historyPath));

        var history = new ViewModePreferenceStore("History.ViewMode", "history-view-mode.json", _historyPath);
        Assert.Equal(MangaViewMode.Grid, history.Load(out _));

        history.Save(MangaViewMode.List, out _);
        Assert.Equal(MangaViewMode.List, history.Load(out _));

        // Library's own stored choice is untouched by the other preference.
        Assert.Equal(MangaViewMode.Grid, Feature().Restore());
    }

    [Fact]
    public void ReSelectingTheCurrentModeNeitherRewritesNorNotifies()
    {
        var feature = Feature();
        feature.Select(MangaViewMode.List);
        var written = File.ReadAllText(_libraryPath);

        var notifications = 0;
        feature.Changed += (_, _) => notifications++;

        feature.Select(MangaViewMode.List);

        Assert.Equal(0, notifications);
        Assert.Equal(written, File.ReadAllText(_libraryPath));
        Assert.Equal(MangaViewMode.List, feature.Mode);
    }

    [Fact]
    public void AMalformedStoredModeFallsBackToGridWithoutThrowing()
    {
        File.WriteAllText(_libraryPath, "{ not json");

        var feature = Feature();
        var restored = feature.Restore();

        Assert.Equal(MangaViewMode.Grid, restored);
        Assert.Equal(MangaViewMode.Grid, feature.Mode);
        Assert.NotNull(feature.LastWarning);

        // A read never rewrites the file.
        Assert.Equal("{ not json", File.ReadAllText(_libraryPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
