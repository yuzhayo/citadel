using System.IO;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History.Tests;

/// <summary>
/// Pinned History ordering, retention and durability. Pin and unpin autosave
/// through the one History store, pinned entries sit above ordinary ones and are
/// exempt from the ordinary retention limit, and unpinning returns an entry to
/// ordinary retention instead of deleting it.
/// </summary>
public sealed class PinnedHistoryFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-history-pin-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _historyPath;
    private readonly ReadingHistory _history;

    public PinnedHistoryFeatureTests()
    {
        Directory.CreateDirectory(_root);
        _historyPath = Path.Combine(_root, "history.json");
        _history = new ReadingHistory(new ReadingHistoryStore(_historyPath));
    }

    private PinnedHistoryFeature Feature() => new(_history);

    [Fact]
    public void PinnedEntriesDisplayAboveOrdinaryEntries()
    {
        _history.Record(Title("One"), Chapter("One"));
        _history.Record(Title("Two"), Chapter("Two"));
        _history.Record(Title("Three"), Chapter("Three"));

        var result = Feature().Toggle(FolderPath("One"));

        Assert.True(result.Succeeded);
        Assert.True(result.Pinned);

        var entries = _history.Read();
        Assert.Equal("One", entries[0].Title);
        Assert.True(entries[0].Pinned);
        Assert.Equal(["One", "Three", "Two"], entries.Select(entry => entry.Title));

        // Unpinning returns it to ordinary history rather than deleting it.
        var unpinned = Feature().Toggle(FolderPath("One"));
        Assert.True(unpinned.Succeeded);
        Assert.False(unpinned.Pinned);
        Assert.Equal(3, _history.Read().Count);
        Assert.False(Feature().IsPinned(FolderPath("One")));
    }

    [Fact]
    public void PinnedEntriesSurviveClearAndDoNotCountTowardRetention()
    {
        for (var index = 1; index <= 20; index++)
        {
            _history.Record(Title(index.ToString("D2")), Chapter(index.ToString("D2")));
        }

        // Ordinary retention keeps the 15 most recent and nothing more.
        Assert.Equal(ReadingHistoryStore.MaximumUnpinnedEntries, _history.Read().Count);

        var feature = Feature();
        Assert.True(feature.Toggle(FolderPath("06")).Succeeded);
        Assert.True(feature.Toggle(FolderPath("07")).Succeeded);
        Assert.Equal(15, _history.Read().Count);

        for (var index = 21; index <= 23; index++)
        {
            _history.Record(Title(index.ToString("D2")), Chapter(index.ToString("D2")));
        }

        var entries = _history.Read();
        Assert.Equal(17, entries.Count);
        Assert.Equal(2, entries.Count(entry => entry.Pinned));
        Assert.Equal(15, entries.Count(entry => !entry.Pinned));

        // Both pinned entries are still the ones chosen, most recent first.
        Assert.Equal(["07", "06"], entries.Take(2).Select(entry => entry.Title));

        new ClearHistoryFeature(_history).Clear();
        Assert.Equal(["07", "06"], _history.Read().Select(entry => entry.Title));
    }

    [Fact]
    public void PinningAutosavesThroughTheSingleHistoryStore()
    {
        _history.Record(Title("One"), Chapter("One"));
        Assert.True(Feature().Toggle(FolderPath("One")).Succeeded);

        // A brand new reader of the same file sees the pin, so there is exactly
        // one authoritative history and no second writer.
        var reloaded = new ReadingHistory(new ReadingHistoryStore(_historyPath));
        Assert.True(Assert.Single(reloaded.Read()).Pinned);

        // Pinning an entry that is no longer in history is a local failure.
        var missing = Feature().Toggle(FolderPath("Never Recorded"));
        Assert.False(missing.Succeeded);
        Assert.NotNull(missing.Error);
    }

    [Fact]
    public void ReopeningATitleUpdatesItsPositionWithoutLosingThePin()
    {
        _history.Record(Title("One"), Chapter("One"));
        _history.Record(Title("Two"), Chapter("Two"));
        Assert.True(Feature().Toggle(FolderPath("One")).Succeeded);

        _history.Record(Title("One"), Chapter("One"));

        var entries = _history.Read();
        Assert.Equal(2, entries.Count);
        var one = entries.Single(entry => entry.Title == "One");
        Assert.True(one.Pinned);
    }

    private static string FolderPath(string name) =>
        Path.Combine(Path.GetTempPath(), "citadel-history-library", name);

    private static MangaTitle Title(string name) =>
        new(name, FolderPath(name), [Chapter(name)]);

    private static ChapterInfo Chapter(string name) =>
        new(name + " Chapter 1", Path.Combine(FolderPath(name), name + " Chapter 1.cbz"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
