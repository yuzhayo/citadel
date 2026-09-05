namespace Module.Mangareader.Library;

/// <summary>
/// The single owner of MangaReader's configured library root for the lifetime
/// of the module. It wraps the existing persistence mechanism and the existing
/// successful-scan rule, and exposes only what consumers actually need: an
/// immutable empty-or-valid root snapshot and a change signal.
///
/// Library commits the root; Downloader only reads it when capturing a target.
/// No consumer may instantiate a second <see cref="LibraryPathStore"/>, read
/// the preference file, or reach into a named control on the Library screen.
/// </summary>
public sealed class LibraryRootContext
{
    private readonly LibraryPathStore _store;
    private readonly LibraryScanPersistence _persistence;
    private readonly object _gate = new();
    private string? _root;

    public LibraryRootContext()
        : this(new LibraryPathStore())
    {
    }

    public LibraryRootContext(LibraryPathStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _persistence = new LibraryScanPersistence(_store);
    }

    /// <summary>Raised after the in-memory root changes to a different value.</summary>
    public event EventHandler? RootChanged;

    /// <summary>The normalized root, or null when none is valid yet.</summary>
    public string? CurrentRoot
    {
        get
        {
            lock (_gate)
            {
                return _root;
            }
        }
    }

    /// <summary>
    /// Adopts the persisted value once at startup. A missing or damaged file is
    /// an ordinary empty result; the returned warning belongs to the caller's
    /// status presentation.
    /// </summary>
    public LibraryPathLoadResult Restore()
    {
        var loaded = _store.Load();
        Commit(loaded.Path);
        return loaded;
    }

    /// <summary>Captures the path being scanned; see <see cref="LibraryScanPersistence"/>.</summary>
    public LibraryScanAttempt BeginScan(string fieldText) => _persistence.BeginScan(fieldText);

    /// <summary>
    /// Ends a scan attempt. Persistence still happens only for a scan that
    /// completed without exception and without cancellation, and the returned
    /// save result still carries the persistence warning. The in-memory root
    /// follows the same rule, so a failed or cancelled scan changes neither the
    /// root nor storage.
    /// </summary>
    public LibraryPathSaveResult CompleteScan(
        LibraryScanAttempt attempt,
        bool succeeded,
        bool cancelled)
    {
        var save = _persistence.CompleteScan(attempt, succeeded, cancelled);
        if (succeeded && !cancelled)
        {
            // A scan that succeeded has a valid root even when writing it down
            // failed; the warning above is the only signal that save needs.
            Commit(LibraryPathStore.Normalize(attempt.CapturedPath));
        }

        return save;
    }

    private void Commit(string? root)
    {
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_root, root, StringComparison.Ordinal);
            _root = root;
        }

        if (changed)
        {
            RootChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
