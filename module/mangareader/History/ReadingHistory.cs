using System.IO;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History;

/// <summary>
/// History's recording owner and the single mutation path into its store. The
/// composition root holds it and routes chapter events here, so recording does
/// not depend on the History screen being instantiated or opened. Clear History
/// and Pinned History are independent commands, but both go through this one
/// owner and therefore through one file and one change signal.
/// </summary>
public sealed class ReadingHistory
{
    private readonly ReadingHistoryStore _store;

    public ReadingHistory(ReadingHistoryStore? store = null) =>
        _store = store ?? new ReadingHistoryStore();

    /// <summary>Raised after every mutation attempt, on the caller's thread.</summary>
    public event EventHandler? Changed;

    public void Record(MangaTitle title, ChapterInfo chapter)
    {
        // A missing, full or locked history file must never stop a chapter
        // from opening, so a failed write still reports the change.
        try
        {
            _store.Record(title, chapter);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pins or unpins one entry. A storage failure is left to the caller to
    /// report, because a pin the user cannot see applied is not a silent no-op.
    /// </summary>
    public bool SetPinned(string titleFolderPath, bool pinned)
    {
        var changed = _store.SetPinned(titleFolderPath, pinned);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>
    /// Removes ordinary history and returns how many entries went. Throws on a
    /// storage failure so the caller reports it and the durable file stays as it
    /// was.
    /// </summary>
    public int ClearUnpinned()
    {
        var removed = _store.ClearUnpinned();
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public IReadOnlyList<ReadingHistoryEntry> Read() => _store.Read();

    /// <summary>Whether anything exists that Clear History is allowed to remove.</summary>
    public bool HasUnpinned => _store.Read().Any(entry => !entry.Pinned);
}
