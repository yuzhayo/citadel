using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

// Immutable state, query, result, enrichment, local-availability and
// queue-handoff records owned by Catalog Mirror. This file runs no logic and
// performs no I/O, network, rendering, or dependency on Downloader internals:
// it is the vocabulary the thin coordinator, the query/sync/detail owners and
// the composition root share.

/// <summary>
/// A snapshot lifecycle failure: contract violation, exceeded bound, corrupt
/// or newer schema, or missing file. The store throws it without mutating the
/// checkpoint or the active snapshot, so the caller can stop the sync and
/// report a local error while the previous data stays visible.
/// </summary>
public sealed class CatalogSnapshotException : InvalidOperationException
{
    public CatalogSnapshotException(string message)
        : base(message)
    {
    }

    public CatalogSnapshotException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Complete offline filter set for the first Catalog version (plan 3.2).</summary>
public enum CatalogContentRating
{
    Safe,
    Suggestive,
    Erotica,
    Pornographic,
}

/// <summary>Complete offline sort set for the first Catalog version (plan 3.2).</summary>
public enum CatalogTitleSort
{
    LatestUpdate,
    TitleAsc,
    TitleDesc,
    YearNewest,
    YearOldest,
    LatestChapterHighest,
    LatestChapterLowest,
}

/// <summary>
/// One local filter value. Values selected inside one category match any of
/// that category; different non-empty categories are combined by SQL AND.
/// </summary>
public sealed record CatalogMirrorFilter(
    IReadOnlyList<CatalogContentRating> Ratings,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Genres,
    int? YearFrom,
    int? YearTo,
    int? MinLatestChapter)
{
    public CatalogMirrorFilter(
        IReadOnlyList<CatalogContentRating> ratings,
        IReadOnlyList<string> types,
        IReadOnlyList<string> statuses,
        IReadOnlyList<string> languages,
        int? yearFrom,
        int? yearTo,
        int? minLatestChapter)
        : this(ratings, types, statuses, languages, [], yearFrom, yearTo, minLatestChapter)
    {
    }

    public static CatalogMirrorFilter Empty { get; } = new([], [], [], [], [], null, null, null);
}

public sealed record CatalogGenreOption(string Key, string DisplayName);

public enum CatalogGenreSyncState
{
    Unavailable,
    ReadyToSync,
    Syncing,
    Stopping,
    Stopped,
    Error,
    Ready,
}

public sealed record CatalogGenreSyncProgress(
    CatalogGenreSyncState State,
    string? TitleName,
    long ProcessedTitles,
    long AvailableTitles,
    long TaggedTitles,
    string? ErrorMessage);

/// <summary>One committed title waiting for detail-driven genre enrichment.</summary>
public sealed record CatalogGenreWorkItem(
    string GenerationId,
    string SourceId,
    string TitleId,
    string TitleHid,
    string Title,
    string CanonicalUrl);

/// <summary>Current durable counts for one enrichment generation.</summary>
public sealed record CatalogGenreWorkCounts(
    long ProcessedTitles,
    long AvailableTitles,
    long TaggedTitles,
    string GenerationState);

/// <summary>Paging constants. The local page size is fixed at 40 cards.</summary>
public static class CatalogMirrorPaging
{
    public const int DefaultPageSize = 40;
}

/// <summary>One local query. Search covers display titles and alternate titles.</summary>
public sealed record CatalogMirrorQueryRequest(
    string? Search,
    CatalogMirrorFilter Filter,
    CatalogTitleSort Sort,
    int Page)
{
    public static CatalogMirrorQueryRequest Default { get; } =
        new(null, CatalogMirrorFilter.Empty, CatalogTitleSort.LatestUpdate, 1);
}

/// <summary>One immutable local result page.</summary>
public sealed record CatalogMirrorResultPage(
    IReadOnlyList<CatalogSnapshotItem> Items,
    int TotalResults,
    int Page,
    int TotalPages);

/// <summary>Visible Catalog states (plan 7.1).</summary>
public enum CatalogSyncState
{
    Empty,
    Loading,
    Syncing,
    Stopping,
    Stopped,
    Error,
    Ready,
}

/// <summary>One immutable sync progress snapshot for the action bar.</summary>
public sealed record CatalogSyncProgress(
    CatalogSyncState State,
    string? PartitionKey,
    int PartitionIndex,
    int PartitionCount,
    int Page,
    long StagedRecords,
    long UniqueTitles,
    DateTimeOffset? LastSyncedUtc,
    int WarningCount,
    string? ErrorMessage,
    IReadOnlyList<CatalogPartitionProgress> Partitions);

/// <summary>Committed progress of one partition inside a traversal.</summary>
public sealed record CatalogPartitionProgress(
    string PartitionKey,
    int PartitionIndex,
    int Page,
    long StagedRecords,
    long? ProviderTotal);

/// <summary>
/// Durable checkpoint of an interrupted traversal. The fingerprint guards
/// against pagination loops: the same identity fingerprint on two consecutive
/// pages of one partition is a contract error, never a silent skip.
/// </summary>
public sealed record CatalogSnapshotCheckpoint(
    int SchemaVersion,
    string StagingSnapshotId,
    int PartitionIndex,
    string PartitionKey,
    int Page,
    long StagedRecords,
    string? LastPageFingerprint,
    DateTimeOffset UpdatedAtUtc,
    bool IsRefresh);

/// <summary>
/// The small file activation swaps atomically. It never points at staging.
/// </summary>
public sealed record CatalogSnapshotManifest(
    int SchemaVersion,
    string ActiveSnapshotId,
    long UniqueTitles,
    long PlaceholderTitles,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? LastDeltaUtc,
    IReadOnlyList<string> Warnings);

/// <summary>Terminal activation outcome. Cleanup problems arrive as warnings.</summary>
public sealed record CatalogActivationResult(
    string SnapshotId,
    long UniqueTitles,
    long DuplicatesSuppressed,
    long PlaceholderTitles,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> CleanupWarnings);

/// <summary>
/// Combined immutable mirror state the thin coordinator publishes. Results is
/// null before any snapshot loads; Detail is null until a title opens.
/// IsPartial marks results read from an in-progress building generation.
/// </summary>
public sealed record CatalogMirrorState(
    CatalogMirrorQueryRequest Query,
    CatalogMirrorResultPage? Results,
    CatalogSyncProgress Sync,
    CatalogDetailState? Detail,
    bool IsPartial,
    CatalogGenreSyncProgress? GenreSync = null);

/// <summary>One source group row on an open title.</summary>
public sealed record CatalogDetailGroup(string GroupId, string DisplayName);

/// <summary>One chapter row with its local verdict and selection.</summary>
public sealed record CatalogDetailChapter(
    string ChapterId,
    string DisplayName,
    bool IsAvailableLocally,
    bool IsSelected)
{
    /// <summary>Row text for the availability column; empty when remote-only.</summary>
    public string AvailabilityText => IsAvailableLocally ? "On device" : string.Empty;
}

/// <summary>
/// Render-ready title display for the shared detail composition. Built by the
/// detail owner from the fetched provider detail; the view only assigns it.
/// Metadata rides as provider-neutral options so this contract stays free of
/// presentation assemblies.
/// </summary>
public sealed record CatalogDetailDisplay(
    string Title,
    string? Synopsis,
    string? GenresText,
    IReadOnlyList<RemoteOption> Metadata,
    string? CoverPath);

/// <summary>Explicit open-title state: one group active, selection owned here.</summary>
public sealed record CatalogDetailState(
    CatalogSnapshotItem Title,
    CatalogDetailDisplay? Display,
    IReadOnlyList<CatalogDetailGroup> Groups,
    string SelectedGroupId,
    IReadOnlyList<CatalogDetailChapter> Chapters,
    bool IsLoading,
    string? ErrorMessage);

/// <summary>Narrow folder-confirmation request for one queue handoff.</summary>
public sealed record CatalogQueueTargetRequest(
    string SourceId,
    string TitleId,
    string TitleHid,
    string TitleDisplayName);

/// <summary>
/// One cached title detail. Chapter data is intentionally absent: detail,
/// groups and chapters are fetched online on every explicit title open, so
/// only title-level metadata is cached here.
/// </summary>
public sealed record CatalogTitleEnrichment(
    string SourceId,
    string TitleId,
    string TitleHid,
    string DisplayName,
    string? Synopsis,
    DateTimeOffset FetchedAtUtc);

/// <summary>One chapter the availability verdict must cover.</summary>
public sealed record CatalogAvailabilityChapter(string ChapterId, string ChapterNumber);

/// <summary>
/// Narrow local-availability request. Issued only after a chapter group loads
/// successfully, and only for the selected title and group.
/// </summary>
public sealed record CatalogLocalAvailabilityRequest(
    string SourceId,
    string TitleId,
    string TitleHid,
    string TitleDisplayName,
    string GroupId,
    IReadOnlyList<CatalogAvailabilityChapter> Chapters);

/// <summary>Per-chapter local verdict for the request above.</summary>
public sealed record CatalogChapterAvailability(string ChapterId, bool Available);

/// <summary>Local-availability answer for the request above.</summary>
public sealed record CatalogLocalAvailabilityResult(
    IReadOnlyList<CatalogChapterAvailability> Chapters);

/// <summary>One selected chapter in a queue handoff.</summary>
public sealed record CatalogChapterSelection(string ChapterId, string ChapterNumber, string DisplayName);

/// <summary>
/// Immutable queue-handoff request the composition root translates into one
/// existing <c>DownloadQueueFeature.QueueChapters</c> call. The mirror never
/// writes the queue itself.
/// </summary>
public sealed record CatalogQueueHandoffRequest(
    string SourceId,
    string TitleId,
    string TitleHid,
    string TitleDisplayName,
    string GroupId,
    string GroupDisplayName,
    IReadOnlyList<CatalogChapterSelection> Chapters,
    string TargetFolder);
