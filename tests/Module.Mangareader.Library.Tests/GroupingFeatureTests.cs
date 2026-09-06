using System.IO;
using Module.Mangareader.Library;
using Module.Mangareader.Library.Grouping;

namespace Module.Mangareader.Library.Tests;

/// <summary>
/// Grouping's own contract: definitions and membership autosave atomically,
/// survive a restart whether empty or populated, never drop a title whose folder
/// is missing, and filter subtractively without ever touching the filesystem.
/// </summary>
public sealed class GroupingFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "citadel-library-grouping-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _storagePath;

    public GroupingFeatureTests()
    {
        Directory.CreateDirectory(_root);
        _storagePath = Path.Combine(_root, "library-groups.json");
    }

    private GroupingFeature Feature(ILibraryPathFileIO? fileIO = null) =>
        new(new GroupingStore(_storagePath, fileIO));

    [Fact]
    public void EmptyAndPopulatedGroupsBothSurviveARestart()
    {
        var first = Feature();
        var empty = first.CreateGroup("Watching", []);
        var populated = first.CreateGroup("Finished", ["Title A", "Title B"]);

        Assert.True(empty.Succeeded);
        Assert.True(populated.Succeeded);
        Assert.True(File.Exists(_storagePath));

        var restored = Feature();
        restored.Restore();

        Assert.Equal(2, restored.Groups.Count);
        var watching = restored.Groups.Single(group => group.Name == "Watching");
        Assert.Empty(watching.TitleFolderNames);

        var finished = restored.Groups.Single(group => group.Name == "Finished");
        Assert.Equal(["Title A", "Title B"], finished.TitleFolderNames);
    }

    [Fact]
    public void ARecordedTitleThatIsNotLoadedStaysRecordedAndIsReportedUnavailable()
    {
        var feature = Feature();
        var created = feature.CreateGroup("Long list", ["Present", "Moved Away", "Deleted"]);
        feature.SelectGroup(created.GroupId);

        Assert.Equal(["Moved Away", "Deleted"], feature.UnavailableTitles(["Present", "Unrelated"]));

        // Nothing was dropped: a restart still records all three memberships.
        var restored = Feature();
        restored.Restore();
        var group = restored.Groups.Single();
        Assert.Equal(3, group.TitleFolderNames.Count);

        // The active tab is session state, so it is chosen again after a restart.
        restored.SelectGroup(group.Id);
        Assert.Equal(2, restored.UnavailableTitles(["Present"]).Count);
    }

    [Fact]
    public void AGroupNameIsRequiredAndNothingIsPersistedWithoutOne()
    {
        var feature = Feature();

        var blank = feature.CreateGroup("   ", ["Title A"]);

        Assert.False(blank.Succeeded);
        Assert.NotNull(blank.Error);
        Assert.Empty(feature.Groups);
        Assert.False(File.Exists(_storagePath));
    }

    [Fact]
    public void MalformedStoredStateIsIgnoredLocallyAndTheFileIsKept()
    {
        File.WriteAllText(_storagePath, "{ this is not json");

        var feature = Feature();
        var loaded = feature.Restore();

        Assert.Empty(loaded.Groups);
        Assert.NotNull(loaded.Warning);
        Assert.Empty(feature.Groups);

        // A read never rewrites the file, so the user's data is not destroyed.
        Assert.Equal("{ this is not json", File.ReadAllText(_storagePath));
    }

    [Fact]
    public void AddingTheSameTitleTwiceKeepsOneMembershipAndAutosaves()
    {
        var feature = Feature();
        var created = feature.CreateGroup("Reading", []);
        var groupId = created.GroupId!;

        Assert.True(feature.AddTitleToGroup(groupId, "Title A").Succeeded);
        Assert.True(feature.AddTitleToGroup(groupId, "Title A").Succeeded);
        Assert.True(feature.AddTitleToGroup(groupId, "title a").Succeeded);

        var restored = Feature();
        restored.Restore();
        Assert.Equal(["Title A"], restored.Groups.Single().TitleFolderNames);
    }

    [Fact]
    public void TheGroupFilterIsSubtractiveAndUnfilteredByDefault()
    {
        var feature = Feature();
        var created = feature.CreateGroup("Only A", ["Title A"]);

        // Creating a group changes the tabs, never what is currently visible.
        Assert.Null(feature.ActiveGroupId);
        Assert.True(feature.IsVisible("Title A"));
        Assert.True(feature.IsVisible("Title B"));

        feature.SelectGroup(created.GroupId);
        Assert.True(feature.IsVisible("Title A"));
        Assert.False(feature.IsVisible("Title B"));

        feature.SelectGroup(null);
        Assert.True(feature.IsVisible("Title B"));

        // Selecting a group that does not exist changes nothing.
        feature.SelectGroup("missing-id");
        Assert.Null(feature.ActiveGroupId);
    }

    /// <summary>
    /// A group the store rejected must not appear. Adopting it anyway would show a
    /// tab the next restart does not have, so the previous collection stays, the
    /// failure is reported to the command that raised it, and Changed still fires
    /// once so the warning renders.
    /// </summary>
    [Fact]
    public void AGroupWhoseSaveWasRejectedIsNotAdoptedAndThePreviousGroupsStayVisible()
    {
        var saving = Feature();
        Assert.True(saving.CreateGroup("Reading", ["Title A"]).Succeeded);

        var failing = Feature(new FailingFileIO());
        failing.Restore();
        Assert.Equal(["Reading"], failing.Groups.Select(group => group.Name));

        var notifications = 0;
        failing.Changed += (_, _) => notifications++;

        var rejected = failing.CreateGroup("Not saved", ["Title B"]);

        Assert.False(rejected.Succeeded);
        Assert.NotNull(rejected.Error);
        Assert.NotNull(failing.LastWarning);
        Assert.Equal(["Reading"], failing.Groups.Select(group => group.Name));
        Assert.Equal(1, notifications);

        // The rejected group never reached the file either, so a restart agrees
        // with what the tab strip was showing.
        var restored = Feature();
        restored.Restore();
        Assert.Equal(["Reading"], restored.Groups.Select(group => group.Name));
    }

    /// <summary>
    /// The store's own injectable file seam, failing on write only. Reads still go
    /// to the real file, so a restore before the failure behaves normally.
    /// </summary>
    private sealed class FailingFileIO : ILibraryPathFileIO
    {
        public string ReadAllText(string path) => File.ReadAllText(path);

        public void WriteAllText(string path, string content) =>
            throw new IOException("disk full");

        public void Move(string sourcePath, string destinationPath, bool overwrite) =>
            throw new IOException("disk full");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
