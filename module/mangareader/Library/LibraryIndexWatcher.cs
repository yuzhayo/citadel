using System.IO;

namespace Module.Mangareader.Library;

/// <summary>One title folder that needs a reindex (created, changed, renamed
/// in) or a drop (deleted, renamed out).</summary>
public sealed class LibraryTitleChangedEventArgs : EventArgs
{
    public LibraryTitleChangedEventArgs(string folderPath, bool removed)
    {
        FolderPath = folderPath;
        Removed = removed;
    }

    public string FolderPath { get; }

    public bool Removed { get; }
}

/// <summary>
/// The watcher's half of library freshness: a bonus, never the authority.
/// The startup shallow reconciliation (coordinator) is the primary source —
/// network drives and USB libraries routinely swallow watcher events — so a
/// missed event only delays an update the next Scan or restart would catch.
///
/// Watches the root tree and maps every event up to its first-level title
/// folder; per-folder quiet-period coalescing means a burst of writes to one
/// title reindexes it once. Callbacks arrive on pool threads; the view
/// marshals to the UI thread. Dispose stops everything: no event or reindex
/// outlives the view.
/// </summary>
public sealed class LibraryIndexWatcher : IDisposable
{
    private readonly string _root;
    private readonly TimeSpan _quietPeriod;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Stamp, bool Removed)> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public LibraryIndexWatcher(string root, TimeSpan? quietPeriod = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root.Trim());
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(750);

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.CreationTime,
            EnableRaisingEvents = Directory.Exists(_root),
        };
        _watcher.Created += (_, e) => Record(e.FullPath, removed: false);
        _watcher.Changed += (_, e) => Record(e.FullPath, removed: false);
        _watcher.Deleted += (_, e) => Record(e.FullPath, removed: true);
        _watcher.Renamed += (_, e) =>
        {
            Record(e.OldFullPath, removed: true);
            Record(e.FullPath, removed: false);
        };
        _watcher.Error += (_, _) => RestartWatcher();

        _debounce = new Timer(Flush, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<LibraryTitleChangedEventArgs>? TitleChanged;

    /// <summary>
    /// Raised after the watcher restarts itself following an internal error
    /// (typically a buffer overflow during a burst of writes). Events lost in
    /// the overflow are unknowable, so the consumer should run one quiet
    /// reconciliation instead of trusting the event stream.
    /// </summary>
    public event EventHandler? ResyncRequested;

    /// <summary>
    /// Maps any path under the root to its first-level title folder.
    /// Null when the path is the root itself or outside the root.
    /// </summary>
    internal static string? ResolveTitleFolder(string root, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException)
        {
            return null;
        }

        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var relative = normalized[prefix.Length..];
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var first = separator < 0 ? relative : relative[..separator];
        if (first.Length == 0) return null;

        return prefix + first;
    }

    internal void Record(string fullPath, bool removed)
    {
        var folder = ResolveTitleFolder(_root, fullPath);
        if (folder is null) return;

        lock (_gate)
        {
            if (_disposed) return;
            if (_pending.TryGetValue(folder, out var prior) && !removed && !prior.Removed)
            {
                // Coalesce a burst of changes to the first stamp: one folder,
                // one reindex.
            }
            else
            {
                // A removal wins over a queued change; a change after a queued
                // removal means the folder came back.
                _pending[folder] = (DateTime.UtcNow, removed);
            }

            _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Folders quiet for at least the quiet period, oldest first. Pure clock
    /// input, so coalescing is unit-testable without timing tricks.
    /// </summary>
    internal IReadOnlyList<(string FolderPath, bool Removed)> CollectDue(DateTime utcNow)
    {
        lock (_gate)
        {
            var due = _pending
                .Where(pair => (utcNow - pair.Value.Stamp) >= _quietPeriod)
                .OrderBy(pair => pair.Value.Stamp)
                .ToArray();

            var results = new List<(string, bool)>(due.Length);
            foreach (var pair in due)
            {
                _pending.Remove(pair.Key);
                results.Add((pair.Key, pair.Value.Removed));
            }

            return results;
        }
    }

    private void Flush(object? state)
    {
        List<(string FolderPath, bool Removed)> due;
        lock (_gate)
        {
            if (_disposed) return;
            due = CollectDue(DateTime.UtcNow).ToList();
        }

        foreach (var (folderPath, removed) in due)
        {
            TitleChanged?.Invoke(this, new LibraryTitleChangedEventArgs(folderPath, removed));
        }
    }

    private void RestartWatcher()
    {
        bool alive;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.EnableRaisingEvents = Directory.Exists(_root);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
            }

            alive = !_disposed && _watcher.EnableRaisingEvents;
        }

        // Outside the gate: a resync runs reconciliation, which must never
        // block watcher teardown or a concurrent flush.
        if (alive) ResyncRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
        }

        _debounce.Dispose();
        _watcher.Dispose();
    }
}
