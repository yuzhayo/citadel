namespace Module.Mangareader.Library;

/// <summary>
/// The path captured when a scan starts. Completion can only persist this
/// captured value, which makes it structurally impossible for a completion
/// to persist whatever the field happens to say at that later moment.
/// </summary>
public sealed record LibraryScanAttempt(string CapturedPath);

/// <summary>
/// The pure rule that the Library view applies around every scan attempt:
/// the path is captured once at scan start, and persistence is written only
/// for a scan that completed without exception and without cancellation.
/// A failed or cancelled scan leaves the previous saved value untouched.
/// Kept separate from the view so the rule is testable without WPF or
/// source-text assertions.
/// </summary>
public sealed class LibraryScanPersistence
{
    private readonly LibraryPathStore _store;

    public LibraryScanPersistence(LibraryPathStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Captures the scanned path at scan start. Later completions never see
    /// the field again, so a field edited mid-scan cannot leak into what
    /// gets persisted.
    /// </summary>
    public LibraryScanAttempt BeginScan(string fieldText) =>
        new((fieldText ?? string.Empty).Trim());

    public LibraryPathSaveResult CompleteScan(
        LibraryScanAttempt attempt,
        bool succeeded,
        bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!succeeded || cancelled)
        {
            return new LibraryPathSaveResult(false, null);
        }

        return _store.Save(attempt.CapturedPath);
    }
}
