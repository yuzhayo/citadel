using System.IO;

namespace Module.Mangareader.History;

public sealed record ClearHistoryResult(bool Succeeded, int Removed, string? Error);

/// <summary>
/// The one owner of the Clear History command. It removes ordinary history only
/// and mutates through the single History owner, so it can never delete a pinned
/// entry, a manga file or a Library entry, and it can never become a second
/// persistence writer beside Pinned History.
/// </summary>
public sealed class ClearHistoryFeature
{
    private readonly ReadingHistory _history;
    private readonly object _gate = new();
    private bool _running;

    public ClearHistoryFeature(ReadingHistory history) =>
        _history = history ?? throw new ArgumentNullException(nameof(history));

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>
    /// Enabled only when something removable exists and no clear is in flight,
    /// so the action can never be pressed into a second concurrent mutation.
    /// </summary>
    public bool CanClear
    {
        get { lock (_gate) return !_running && _history.HasUnpinned; }
    }

    public ClearHistoryResult Clear()
    {
        lock (_gate)
        {
            if (_running)
            {
                return new ClearHistoryResult(false, 0, "Clear History sedang berjalan.");
            }

            if (!_history.HasUnpinned)
            {
                return new ClearHistoryResult(false, 0, "Tidak ada history yang bisa dihapus.");
            }

            _running = true;
        }

        try
        {
            return new ClearHistoryResult(true, _history.ClearUnpinned(), null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            // The store writes atomically, so a failure here leaves the durable
            // history exactly as it was and the error stays inside History.
            return new ClearHistoryResult(
                false,
                0,
                "History tidak dapat dihapus: " + exception.GetBaseException().Message);
        }
        finally
        {
            lock (_gate) _running = false;
        }
    }
}
