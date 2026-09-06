using System.IO;

namespace Module.Mangareader.History;

public sealed record PinResult(bool Succeeded, bool Pinned, string? Error);

/// <summary>
/// The one owner of the pin command. Pinning autosaves through the same single
/// History owner that recording and clearing use, so this feature never opens a
/// second history file and never competes with Clear History for a write.
///
/// Pinned entries stay above ordinary ones, survive Clear History and are exempt
/// from ordinary retention. Unpinning returns an entry to ordinary retention
/// rather than deleting it.
/// </summary>
public sealed class PinnedHistoryFeature
{
    private readonly ReadingHistory _history;

    public PinnedHistoryFeature(ReadingHistory history) =>
        _history = history ?? throw new ArgumentNullException(nameof(history));

    public bool IsPinned(string titleFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleFolderPath);
        return Find(titleFolderPath)?.Pinned ?? false;
    }

    public PinResult Toggle(string titleFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleFolderPath);

        var entry = Find(titleFolderPath);
        if (entry is null)
        {
            return new PinResult(false, false, "Entri history ini sudah tidak ada.");
        }

        try
        {
            var pinned = !entry.Pinned;
            return _history.SetPinned(titleFolderPath, pinned)
                ? new PinResult(true, pinned, null)
                : new PinResult(false, entry.Pinned, "Entri history ini sudah tidak ada.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            // A pin the user cannot see applied is not a silent no-op: report it
            // and leave the durable file untouched.
            return new PinResult(
                false,
                entry.Pinned,
                "Pin tidak dapat disimpan: " + exception.GetBaseException().Message);
        }
    }

    private ReadingHistoryEntry? Find(string titleFolderPath) =>
        _history.Read().FirstOrDefault(entry => string.Equals(
            entry.TitleFolderPath,
            titleFolderPath,
            StringComparison.OrdinalIgnoreCase));
}
