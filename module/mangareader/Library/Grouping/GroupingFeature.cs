namespace Module.Mangareader.Library.Grouping;

/// <summary>
/// The result of one grouping command. A failure is local: the group collection
/// keeps its previous value and the durable file is left as it was.
/// </summary>
public sealed record GroupingOutcome(bool Succeeded, string? GroupId, string? Error);

/// <summary>
/// Owns Library's group definitions, the active group filter and their
/// persistence. UI-free: it works on title folder names, which is the identity
/// Library already gives a title, so it never holds a card model, never scans
/// the Library and never touches a folder or an archive.
///
/// Membership is durable and tolerant. A title whose folder is currently missing
/// stays recorded and is reported as unavailable instead of being dropped.
/// </summary>
public sealed class GroupingFeature
{
    private readonly GroupingStore _store;
    private readonly object _gate = new();

    private List<LibraryGroup> _groups = [];
    private string? _activeGroupId;
    private string? _lastWarning;

    public GroupingFeature(GroupingStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Raised after any state change, on the caller's thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<LibraryGroup> Groups
    {
        get
        {
            lock (_gate)
            {
                return [.. _groups];
            }
        }
    }

    /// <summary>Null means no group filter, so every loaded title is visible.</summary>
    public string? ActiveGroupId
    {
        get
        {
            lock (_gate)
            {
                return _activeGroupId;
            }
        }
    }

    /// <summary>The most recent persistence warning, or null.</summary>
    public string? LastWarning
    {
        get
        {
            lock (_gate)
            {
                return _lastWarning;
            }
        }
    }

    /// <summary>
    /// Adopts the stored definitions once. A damaged file is an ordinary empty
    /// result plus a warning; the file is not rewritten by reading it.
    /// </summary>
    public GroupingLoadResult Restore()
    {
        var loaded = _store.Load();
        lock (_gate)
        {
            _groups = [.. loaded.Groups];
            _lastWarning = loaded.Warning;
            // A stored active group that no longer exists falls back to no filter.
            if (_activeGroupId is not null && Find(_activeGroupId) is null)
            {
                _activeGroupId = null;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return loaded;
    }

    public void SelectGroup(string? groupId)
    {
        lock (_gate)
        {
            if (groupId is not null && Find(groupId) is null) return;
            if (string.Equals(_activeGroupId, groupId, StringComparison.Ordinal)) return;
            _activeGroupId = groupId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates one group. A name is required; titles are optional, so an empty
    /// group is valid and survives a restart like any other.
    /// </summary>
    public GroupingOutcome CreateGroup(string name, IReadOnlyList<string>? titleFolderNames)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new GroupingOutcome(false, null, "A group name is required.");
        }

        var group = new LibraryGroup(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            Normalize(titleFolderNames));

        return Commit(groups => groups.Add(group), group.Id);
    }

    /// <summary>
    /// Adds one title to one existing group. Membership is a set, so adding a
    /// title that is already a member changes nothing and still succeeds.
    /// </summary>
    public GroupingOutcome AddTitleToGroup(string groupId, string titleFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleFolderName);

        lock (_gate)
        {
            if (Find(groupId) is null)
            {
                return new GroupingOutcome(false, null, "That group no longer exists.");
            }
        }

        return Commit(groups =>
        {
            var index = groups.FindIndex(candidate =>
                string.Equals(candidate.Id, groupId, StringComparison.Ordinal));
            var titles = new List<string>(groups[index].TitleFolderNames);
            if (!titles.Contains(titleFolderName, StringComparer.OrdinalIgnoreCase))
            {
                titles.Add(titleFolderName);
            }

            groups[index] = groups[index] with { TitleFolderNames = titles };
        }, groupId);
    }

    /// <summary>
    /// Whether a loaded title passes the active filter. With no active group
    /// every title is visible, so the filter is purely subtractive.
    /// </summary>
    public bool IsVisible(string titleFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleFolderName);
        lock (_gate)
        {
            if (_activeGroupId is null) return true;
            return Find(_activeGroupId) is { } group
                && group.TitleFolderNames.Contains(titleFolderName, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Recorded members of the active group that are not in the loaded title
    /// list. They stay recorded; this only lets the presentation show them as
    /// unavailable rather than pretending they were removed.
    /// </summary>
    public IReadOnlyList<string> UnavailableTitles(IEnumerable<string> loadedFolderNames)
    {
        ArgumentNullException.ThrowIfNull(loadedFolderNames);
        var loaded = new HashSet<string>(loadedFolderNames, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            if (_activeGroupId is null || Find(_activeGroupId) is not { } group) return [];
            return [.. group.TitleFolderNames.Where(name => !loaded.Contains(name))];
        }
    }

    /// <summary>
    /// Builds the next collection from the current one, saves it, and adopts it only
    /// when the save succeeded. A rejected save keeps the previous groups, because
    /// showing a group the next restart will not have is worse than reporting the
    /// failure; the warning is recorded either way and <see cref="Changed"/> is
    /// raised either way so it renders.
    /// </summary>
    private GroupingOutcome Commit(Action<List<LibraryGroup>> mutate, string groupId)
    {
        List<LibraryGroup> next;
        lock (_gate)
        {
            next = [.. _groups];
            mutate(next);
        }

        var save = _store.Save(next);
        lock (_gate)
        {
            if (save.Saved) _groups = next;
            _lastWarning = save.Warning;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new GroupingOutcome(save.Saved, groupId, save.Saved ? null : save.Warning);
    }

    private LibraryGroup? Find(string groupId) =>
        _groups.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, groupId, StringComparison.Ordinal));

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? titleFolderNames)
    {
        if (titleFolderNames is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (var name in titleFolderNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name)) results.Add(name);
        }

        return results;
    }
}
