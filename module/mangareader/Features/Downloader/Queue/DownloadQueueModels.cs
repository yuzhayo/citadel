using System.IO;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>
/// Durable job states. <see cref="Pausing"/> is distinct from
/// <see cref="Paused"/> because a pause must wait for an already-written
/// pyhost command to reach its bounded terminal response before it can claim
/// the job is parked.
/// </summary>
public enum DownloadJobState
{
    Queued,
    Resolving,
    Downloading,
    Recovering,
    AwaitingSourceFallback,
    Decoding,
    Validating,
    Publishing,
    Pausing,
    Paused,
    Failed,
    Completed,
}

/// <summary>
/// Remote identity of one queued chapter. Chapter number is display metadata
/// and is deliberately not part of identity: several groups publish the same
/// number as distinct variants.
/// </summary>
public sealed record DownloadJobIdentity(
    string SourceId,
    string TitleId,
    string TitleHid,
    string ChapterId,
    string GroupId)
{
    public string Key =>
        SourceId + "|" + TitleHid + "|" + ChapterId + "|" + GroupId;
}

/// <summary>
/// The confirmed publication target, captured when the job is queued. Changing
/// the Library root afterwards cannot silently redirect an active job.
/// </summary>
public sealed record DownloadTarget(string Root, string FolderName, string FileName)
{
    public string FolderPath => Path.Combine(Root, FolderName);

    public string FilePath => Path.Combine(FolderPath, FileName);
}

/// <summary>One candidate offered when a whole-chapter group fallback is needed.</summary>
public sealed record SourceFallbackCandidate(
    string GroupId,
    string GroupName,
    string ChapterId,
    string Reason);

/// <summary>
/// The durable queue entry and the immutable snapshot the UI binds. State
/// transitions produce a new record, so a background job can never mutate what
/// a screen is currently rendering.
/// </summary>
public sealed record DownloadJobRecord
{
    public string JobId { get; init; } = string.Empty;

    public DownloadJobIdentity Identity { get; init; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    public string TitleDisplayName { get; init; } = string.Empty;

    public string ChapterDisplayName { get; init; } = string.Empty;

    public string GroupDisplayName { get; init; } = string.Empty;

    public string ChapterNumber { get; init; } = string.Empty;

    public DownloadTarget Target { get; init; } =
        new(string.Empty, string.Empty, string.Empty);

    public DownloadJobState State { get; init; } = DownloadJobState.Queued;

    public string? Warning { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RouteText { get; init; }

    public string? ManifestHash { get; init; }

    public int PageCount { get; init; }

    public int CompletedPages { get; init; }

    /// <summary>Set once publication is confirmed; used to reconcile a crash after rename.</summary>
    public string? PublishedPath { get; init; }

    public DateTimeOffset QueuedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public IReadOnlyList<SourceFallbackCandidate> FallbackCandidates { get; init; } = [];

    public bool IsTerminal => State is DownloadJobState.Completed or DownloadJobState.Failed;

    public bool IsInFlight => State is not (DownloadJobState.Queued
        or DownloadJobState.Paused
        or DownloadJobState.Completed
        or DownloadJobState.Failed
        or DownloadJobState.AwaitingSourceFallback);

    public string StateText => State switch
    {
        DownloadJobState.AwaitingSourceFallback => "Awaiting source",
        DownloadJobState.Pausing => "Stopping",
        DownloadJobState.Paused => "Stopped",
        _ => State.ToString(),
    };

    public string ProgressText => PageCount == 0
        ? "—"
        : CompletedPages + "/" + PageCount;

    /// <summary>
    /// Whether Pause is a valid action. It stays offered while the pause itself is
    /// settling — disabled rather than hidden — so the row visibly parks instead
    /// of losing its action mid-transition.
    /// </summary>
    public bool CanPause => State is DownloadJobState.Queued || IsInFlight;

    /// <summary>Whether the resume action is valid for this state.</summary>
    public bool CanResume => State is DownloadJobState.Paused or DownloadJobState.Failed;

    /// <summary>One action, labelled by what it actually does to this state.</summary>
    public string ResumeActionLabel => State == DownloadJobState.Failed ? "Retry" : "Resume";

    /// <summary>
    /// Whether this row's own operation has settled. A pause must wait for the
    /// already-written bounded command to reach its terminal response before the
    /// row can claim the job is parked, so its actions are disabled meanwhile.
    /// </summary>
    public bool CanActNow => State != DownloadJobState.Pausing;

    public bool CanChooseFallback =>
        State == DownloadJobState.AwaitingSourceFallback && FallbackCandidates.Count > 0;

    public bool CanOpenFolder =>
        State == DownloadJobState.Completed && !string.IsNullOrWhiteSpace(PublishedPath);
}

/// <summary>
/// Per-page staging journal entry, stored in a job's own <c>job.json</c>. This
/// is page evidence only; it never duplicates queue state, which has exactly
/// one owner (<c>queue.json</c>).
/// </summary>
public sealed record StagedPageRecord(
    int Ordinal,
    string RemoteKey,
    string RelativePath,
    long? ExpectedBytes,
    long ObservedBytes,
    string Sha256,
    string Format,
    bool Transformed,
    bool Validated);

/// <summary>Counts behind the Catalog badge. Never includes a hard-coded total.</summary>
public sealed record QueueSummary(int Total, int Active, int Paused, int Failed)
{
    public static QueueSummary Empty { get; } = new(0, 0, 0, 0);

    public int BadgeCount => Active + Paused + Failed;
}
