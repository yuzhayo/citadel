using System.IO;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library;

/// <summary>
/// Outcome of one reconciliation pass: how the in-memory index moved. Counts
/// only — the entries themselves are read back through <see cref="Entries"/>.
/// </summary>
public sealed record LibraryReconcileResult(
    int Added,
    int Updated,
    int Removed,
    int Total,
    string? Warning);

/// <summary>
/// Owner of the in-memory library index. Loads the stored document, keeps it
/// authoritative while the Library is up, and reconciles it against the
/// filesystem without ever enumerating chapter lists library-wide:
/// only folder names plus folder metadata are read, and only folders whose
/// fingerprint moved pay for a full single-title reindex.
///
/// Failure and cancellation keep the previous index: the new entry set is
/// built aside and swapped in only after the pass (and its save) succeeds,
/// so the grid never goes suddenly empty.
///
/// Threading: every public method serializes on one gate. Reconcile runs on
/// a pool thread while Reload, ReindexOne, and snapshots run on the UI
/// thread; without the gate a background pass and a watcher-driven update
/// could corrupt the dictionary or lost-update each other's save. A whole
/// pass holds the gate — watcher events during a pass wait, then apply in
/// order — which is correct and, for a metadata-only pass, brief.
/// </summary>
public sealed class LibraryIndexCoordinator
{
    private readonly LibraryIndexStore _store;
    private readonly LibraryTitleLoader _loader;
    private readonly ILibraryCoverThumbnails? _covers;
    private readonly Dictionary<string, LibraryIndexEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private string? _root;

    public LibraryIndexCoordinator(
        LibraryIndexStore? store = null,
        LibraryTitleLoader? loader = null,
        ILibraryCoverThumbnails? covers = null)
    {
        _store = store ?? new LibraryIndexStore();
        _loader = loader ?? new LibraryTitleLoader();
        _covers = covers;
    }

    public string? Root
    {
        get { lock (_gate) return _root; }
    }

    public LibraryTitleLoader Titles => _loader;

    public IReadOnlyList<LibraryIndexEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .OrderBy(entry => entry.TitleFolderName, NaturalStringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Replaces the in-memory index with the stored document for a root.
    /// An invalid root, a missing file, or a damaged document clears the
    /// index; the caller decides what the grid shows. Fast by design: one
    /// small file, no chapter reads.
    /// </summary>
    public void Reload(string? rawRoot)
    {
        lock (_gate)
        {
            ReloadLocked(LibraryPathStore.Normalize(rawRoot));
        }
    }

    private void ReloadLocked(string? root)
    {
        _root = root;
        _entries.Clear();
        if (root is null) return;

        var loaded = _store.Load(root);
        foreach (var entry in loaded.Entries)
        {
            _entries[entry.FolderPath] = entry;
        }
    }

    /// <summary>
    /// Shallow reconciliation for a root: enumerate title folders, drop
    /// entries whose folder is gone, and reindex only new folders plus
    /// folders whose fingerprint moved. The stored document is rewritten
    /// only when something actually changed.
    /// </summary>
    public LibraryReconcileResult Reconcile(string? rawRoot, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ReconcileLocked(rawRoot, cancellationToken);
        }
    }

    private LibraryReconcileResult ReconcileLocked(string? rawRoot, CancellationToken cancellationToken)
    {
        var root = LibraryPathStore.Normalize(rawRoot);
        if (root is null)
        {
            return new LibraryReconcileResult(0, 0, 0, _entries.Count, "No library folder is selected.");
        }

        // Existence is checked before touching the in-memory index: a gone
        // folder throws while the previous index stays authoritative.
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Library folder was not found: {root}");
        }

        if (!string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
        {
            ReloadLocked(root);
        }

        var folders = Directory
            .GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build aside: cancellation or failure below discards this pass and
        // the previous index stays authoritative.
        var next = new Dictionary<string, LibraryIndexEntry>(_entries, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var updated = 0;
        var removed = 0;

        foreach (var missing in _entries.Keys.Where(path => !folders.Contains(path)).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            next.Remove(missing);
            removed++;
        }

        foreach (var folder in folders.OrderBy(
            path => Path.GetFileName(path) ?? string.Empty,
            NaturalStringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!next.TryGetValue(folder, out var existing))
            {
                var fresh = _loader.BuildEntry(folder, null, cancellationToken);
                if (fresh is null) continue;
                next[folder] = ApplyThumbnail(fresh, cancellationToken);
                added++;
                continue;
            }

            DateTime fingerprint;
            try
            {
                fingerprint = Directory.GetLastWriteTimeUtc(folder);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                continue;
            }

            // Equality means "unchanged". Directory timestamps can share one
            // tick for rapid successive writes, so a change followed by a
            // reconcile inside the same tick is invisible here by design:
            // production reconciles run seconds apart (startup, Scan, quiet
            // background), where ticks always advance, and the watcher path
            // (ReindexOne) never consults fingerprints at all.
            if (fingerprint == existing.FolderFingerprintUtc) continue;

            var rebuilt = _loader.BuildEntry(folder, existing, cancellationToken);
            if (rebuilt is null)
            {
                next.Remove(folder);
                removed++;
                continue;
            }

            next[folder] = ApplyThumbnail(rebuilt, cancellationToken);
            updated++;
        }

        if (added == 0 && updated == 0 && removed == 0)
        {
            return new LibraryReconcileResult(0, 0, 0, next.Count, null);
        }

        var save = _store.Save(root, next.Values.ToArray());
        if (!save.Saved)
        {
            return new LibraryReconcileResult(
                0, 0, 0, _entries.Count,
                $"The reconciled index could not be saved: {save.Warning} Showing the previous index.");
        }

        _entries.Clear();
        foreach (var entry in next.Values)
        {
            _entries[entry.FolderPath] = entry;
        }

        PurgeOrphanThumbnails();

        return new LibraryReconcileResult(added, updated, removed, _entries.Count, null);
    }

    /// <summary>
    /// Reindexes (or drops) one title folder and persists. Null when the
    /// folder holds nothing indexable — the entry is gone in that case.
    /// </summary>
    public LibraryIndexEntry? ReindexOne(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var fullPath = Path.GetFullPath(folderPath.Trim());
            _entries.TryGetValue(fullPath, out var existing);

            var rebuilt = _loader.BuildEntry(fullPath, existing, cancellationToken);
            if (rebuilt is null)
            {
                _entries.Remove(fullPath);
            }
            else
            {
                _entries[fullPath] = ApplyThumbnail(rebuilt, cancellationToken);
                rebuilt = _entries[fullPath];
            }

            if (_root is not null)
            {
                _store.Save(_root, _entries.Values.ToArray());
            }

            PurgeOrphanThumbnails();

            return rebuilt;
        }
    }

    /// <summary>
    /// Resolves an entry's thumbnail through the cache provider. Best
    /// effort: a missing provider or a failed thumbnail leaves the stored
    /// path untouched and the index survives.
    /// </summary>
    private LibraryIndexEntry ApplyThumbnail(LibraryIndexEntry entry, CancellationToken cancellationToken)
    {
        if (_covers is null) return entry;

        string? path;
        try
        {
            path = _covers.EnsureThumbnail(entry, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return entry;
        }

        return string.IsNullOrWhiteSpace(path) ? entry : entry with { CoverThumbnailPath = path };
    }

    /// <summary>
    /// Collects thumbnails no entry references anymore. Orphans are disk
    /// waste only, so a purge failure is swallowed, never surfaced.
    /// </summary>
    private void PurgeOrphanThumbnails()
    {
        if (_covers is null) return;

        try
        {
            _covers.PurgeExcept(_entries.Values
                .Select(entry => entry.CoverThumbnailPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }
}
