using System.IO;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History;

/// <summary>
/// History's recording owner. The composition root holds it and routes chapter
/// events here, so recording does not depend on the History screen being
/// instantiated or opened. The screen subscribes to <see cref="Changed"/> and
/// rebuilds its own presentation from <see cref="Read"/>.
/// </summary>
public sealed class ReadingHistory
{
    private readonly ReadingHistoryStore _store = new();

    /// <summary>Raised after every record attempt, on the caller's thread.</summary>
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

    public IReadOnlyList<ReadingHistoryEntry> Read() => _store.Read();
}
