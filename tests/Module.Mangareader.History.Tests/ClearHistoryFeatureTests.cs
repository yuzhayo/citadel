using System.IO;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History.Tests;

/// <summary>
/// Clear History is one owner performing one mutation through the single History
/// store: it removes ordinary history, preserves pinned entries and the durable
/// file on failure, and refuses to run when nothing is removable.
/// </summary>
public sealed class ClearHistoryFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-history-clear-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _historyPath;
    private readonly ReadingHistory _history;

    public ClearHistoryFeatureTests()
    {
        Directory.CreateDirectory(_root);
        _historyPath = Path.Combine(_root, "history.json");
        _history = new ReadingHistory(new ReadingHistoryStore(_historyPath));
    }

    private ClearHistoryFeature Feature() => new(_history);

    [Fact]
    public void ClearRemovesOnlyUnpinnedHistory()
    {
        _history.Record(Title("One"), Chapter("One"));
        _history.Record(Title("Two"), Chapter("Two"));
        _history.Record(Title("Three"), Chapter("Three"));
        Assert.True(_history.SetPinned(FolderPath("Two"), pinned: true));

        var feature = Feature();
        Assert.True(feature.CanClear);

        var result = feature.Clear();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Removed);
        Assert.Null(result.Error);

        // Only the pinned entry survives, and the manga files are never touched.
        var remaining = Assert.Single(_history.Read());
        Assert.Equal("Two", remaining.Title);
        Assert.True(remaining.Pinned);
        Assert.False(feature.CanClear);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void ClearIsDisabledWhenNothingIsRemovable()
    {
        var feature = Feature();
        Assert.False(feature.CanClear);

        var empty = feature.Clear();
        Assert.False(empty.Succeeded);
        Assert.Equal(0, empty.Removed);
        Assert.NotNull(empty.Error);

        _history.Record(Title("One"), Chapter("One"));
        Assert.True(feature.CanClear);

        _history.SetPinned(FolderPath("One"), pinned: true);
        Assert.False(feature.CanClear);

        var pinnedOnly = feature.Clear();
        Assert.False(pinnedOnly.Succeeded);
        Assert.Equal(0, pinnedOnly.Removed);
        Assert.Single(_history.Read());
    }

    [Fact]
    public void AStorageFailureIsReportedLocallyAndLeavesTheDurableFileIntact()
    {
        _history.Record(Title("One"), Chapter("One"));
        _history.Record(Title("Two"), Chapter("Two"));
        var before = File.ReadAllText(_historyPath);

        // A read-only target still reads fine but cannot be replaced, which is
        // exactly the failure the command must report instead of swallowing.
        File.SetAttributes(_historyPath, FileAttributes.ReadOnly);
        try
        {
            var result = Feature().Clear();

            Assert.False(result.Succeeded);
            Assert.Equal(0, result.Removed);
            Assert.NotNull(result.Error);
            Assert.Equal(before, File.ReadAllText(_historyPath));
            Assert.Equal(2, _history.Read().Count);
        }
        finally
        {
            File.SetAttributes(_historyPath, FileAttributes.Normal);
        }
    }

    private static string FolderPath(string name) =>
        Path.Combine(Path.GetTempPath(), "citadel-history-library", name);

    private static MangaTitle Title(string name) =>
        new(name, FolderPath(name), [Chapter(name)]);

    private static ChapterInfo Chapter(string name) =>
        new(name + " Chapter 1", Path.Combine(FolderPath(name), name + " Chapter 1.cbz"));

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.GetFiles(_root))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
