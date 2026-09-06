using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library;

/// <summary>
/// Library's own Grid/List preference. It owns exactly one persisted key,
/// <see cref="PreferenceKey"/>, and nothing else: choosing a presentation never
/// scans, never filters, never reorders and never touches another feature's
/// preference.
/// </summary>
public sealed class LibraryViewModeFeature
{
    public const string PreferenceKey = "Library.ViewMode";

    private readonly ViewModePreferenceStore _store;
    private readonly object _gate = new();

    private MangaViewMode _mode = ViewModePreferenceStore.DefaultMode;
    private string? _lastWarning;

    public LibraryViewModeFeature(ViewModePreferenceStore? store = null) =>
        _store = store ?? new ViewModePreferenceStore(PreferenceKey, "library-view-mode.json");

    /// <summary>Raised only when the mode actually changed.</summary>
    public event EventHandler? Changed;

    public MangaViewMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
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

    /// <summary>Adopts the stored mode once. A damaged file is the default.</summary>
    public MangaViewMode Restore()
    {
        var mode = _store.Load(out var warning);
        lock (_gate)
        {
            _mode = mode;
            _lastWarning = warning;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return mode;
    }

    /// <summary>
    /// Autosaves the chosen mode. Re-choosing the current mode is a no-op, so a
    /// stray click cannot rewrite the file or re-render the presentation.
    /// </summary>
    public void Select(MangaViewMode mode)
    {
        lock (_gate)
        {
            if (_mode == mode) return;
            _mode = mode;
        }

        _store.Save(mode, out var warning);
        lock (_gate)
        {
            _lastWarning = warning;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
