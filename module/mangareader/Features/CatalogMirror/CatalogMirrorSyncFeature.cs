using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Module.Mangareader.Features.Downloader.Sources.Comix;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

// State machine and four-partition traversal for Start/soft-Stop/Resume. It
// never composes Comix queries, parses provider JSON, touches files directly,
// renders UI, fetches detail/covers, or owns queue state: the source serves
// pages and the store commits them. Callers run Start/Resume off the UI
// thread; progress arrives as immutable records the view marshals to its
// Dispatcher. The provider gate is never held across pages, so online
// Downloader requests interleave fairly with an active sync page by page.
public sealed class CatalogMirrorSyncFeature(
    ICatalogSnapshotSource source,
    CatalogSnapshotStore store)
{
    private readonly ICatalogSnapshotSource _source = source;
    private readonly CatalogSnapshotStore _store = store;
    private readonly object _gate = new();
    private int _stopRequested;
    private int _generation;
    private IReadOnlyList<CatalogPartitionProgress> _partitions = [];
    private CatalogSyncProgress _current = new(
        CatalogSyncState.Empty, null, 0, 0, 0, 0, 0, null, 0, null, []);

    /// <summary>
    /// Base polite delay between provider pages plus up to <see cref="PoliteJitter"/>
    /// of full jitter. Keeps one serial browser session well under abusive
    /// velocity without any coordination protocol.
    /// </summary>
    public static readonly TimeSpan DefaultPoliteDelay = TimeSpan.FromMilliseconds(1000);

    /// <summary>Base backoff step for typed 429/502/503 retries (doubled per attempt).</summary>
    public static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(2);

    private const int MaxFetchAttempts = 4;

    /// <summary>Tunable pacing; tests set it to zero. Never negative.</summary>
    public TimeSpan PoliteDelay { get; set; } = DefaultPoliteDelay;

    /// <summary>Tunable retry base; tests shrink it. Never negative.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = DefaultRetryBaseDelay;

    /// <summary>Immutable progress snapshot, updated on every transition.</summary>
    public CatalogSyncProgress Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<CatalogSyncProgress>? StateChanged;

    /// <summary>
    /// Restores the durable sync state after application restart. A building
    /// generation always wins over the active snapshot because it is the work
    /// Resume must continue; no provider request is made here.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        var checkpoint = await _store.LoadCheckpointAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(generation)) return;

        if (checkpoint is not null)
        {
            _partitions = await _store
                .GetPartitionProgressAsync(checkpoint.StagingSnapshotId, cancellationToken)
                .ConfigureAwait(false);
            if (IsCurrent(generation))
            {
                Publish(Stopped(checkpoint, _source.SnapshotPartitions.Count));
            }

            return;
        }

        var manifest = await _store.TryLoadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(generation)) return;

        Publish(manifest is null
            ? new CatalogSyncProgress(
                CatalogSyncState.Empty, null, 0, _source.SnapshotPartitions.Count,
                0, 0, 0, null, 0, null, [])
            : new CatalogSyncProgress(
                CatalogSyncState.Ready, null, _source.SnapshotPartitions.Count,
                _source.SnapshotPartitions.Count, 0, 0, manifest.UniqueTitles,
                manifest.CapturedAtUtc, manifest.Warnings.Count, null, []));
    }

    /// <summary>
    /// Fresh full sync: discards any interrupted staging, then traverses all
    /// partitions from the start. Supersedes an older running traversal.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _stopRequested, 0);
        return RunAsync(fresh: true, refresh: false, generation, cancellationToken);
    }

    /// <summary>
    /// Continues after the durable checkpoint, resuming in refresh mode when
    /// the checkpoint belongs to a stopped delta. Supersedes an older running
    /// traversal.
    /// </summary>
    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _stopRequested, 0);
        return RunAsync(fresh: false, refresh: false, generation, cancellationToken);
    }

    /// <summary>
    /// Incremental refresh against the active snapshot: seeds staging from it,
    /// then walks latest-first per partition until one full page holds zero new
    /// identities. Supersedes an older running traversal. Deletions stay
    /// invisible; only a full backfill heals them.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _stopRequested, 0);
        return RunAsync(fresh: true, refresh: true, generation, cancellationToken);
    }

    /// <summary>
    /// Logical soft-stop: no token is cancelled, so the in-flight provider
    /// request runs to its terminal response and its page still commits. The
    /// next page never starts.
    /// </summary>
    public void RequestStop()
    {
        Volatile.Write(ref _stopRequested, 1);
        CatalogSyncProgress? stopping = null;
        lock (_gate)
        {
            if (_current.State == CatalogSyncState.Syncing)
            {
                stopping = _current with { State = CatalogSyncState.Stopping };
            }
        }

        if (stopping is not null)
        {
            Publish(stopping);
        }
    }

    private async Task RunAsync(bool fresh, bool refresh, int generation, CancellationToken cancellationToken)
    {
        HashSet<string>? known = null;
        CatalogSnapshotCheckpoint? checkpoint;
        if (refresh && fresh)
        {
            // Fresh delta always re-seeds from the active snapshot; a stopped
            // delta is continued via Resume, never by re-seeding over it.
            // Seeding streams the baseline once and returns its identity keys,
            // so the active file is never read twice.
            try
            {
                (checkpoint, known) = await _store
                    .SeedStagingFromActiveAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Publish(Failed(null, 0, null, 0, 0, exception));
                return;
            }
        }
        else
        {
            if (fresh)
            {
                await _store.ClearStagingAsync(cancellationToken).ConfigureAwait(false);
            }

            checkpoint = await _store.LoadCheckpointAsync(cancellationToken).ConfigureAwait(false);
            if (checkpoint?.IsRefresh == true)
            {
                // Resuming a delta rebuilds the known set from the baseline
                // plus the already-staged delta lines, so identities committed
                // before the stop count as known and overlap stops on time.
                refresh = true;
                try
                {
                    known = await _store
                        .LoadRefreshBaselineAsync(checkpoint.StagingSnapshotId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Publish(Failed(checkpoint, 0, checkpoint.PartitionKey, checkpoint.PartitionIndex, checkpoint.Page, exception));
                    return;
                }
            }
        }

        var partitions = _source.SnapshotPartitions;
        // A seed checkpoint sits before partition 0; traversal always starts
        // at a real partition.
        var startPartition = checkpoint is null ? 0 : Math.Max(0, checkpoint.PartitionIndex);
        var startPage = checkpoint is null ? 1 : checkpoint.Page + 1;
        CatalogSnapshotCheckpoint? last = checkpoint;
        await RefreshPartitionsAsync(
            last?.StagingSnapshotId, generation, cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(generation))
        {
            return;
        }

        Publish(new CatalogSyncProgress(
            CatalogSyncState.Syncing, null, startPartition, partitions.Count,
            0, checkpoint?.StagedRecords ?? 0, 0, null, 0, null, _partitions));

        for (var index = startPartition; index < partitions.Count; index++)
        {
            var partition = partitions[index];
            var fetchedAny = index != startPartition || startPage > 1;
            for (var page = index == startPartition ? startPage : 1; ; page++)
            {
                if (!IsCurrent(generation))
                {
                    return;
                }

                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    Publish(Stopped(last, partitions.Count));
                    return;
                }

                if (fetchedAny)
                {
                    // Polite pacing between provider pages. Interruptible: a
                    // wait is not an in-flight request, so Stop wakes it.
                    if (!await WaitInterruptiblyAsync(
                        PoliteDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)),
                        generation).ConfigureAwait(false))
                    {
                        Publish(Stopped(last, partitions.Count));
                        return;
                    }
                }

                CatalogSnapshotPage fetched;
                try
                {
                    var result = await FetchWithBackoffAsync(
                            partition, page, generation, refresh, cancellationToken)
                        .ConfigureAwait(false);
                    if (result is null)
                    {
                        Publish(Stopped(last, partitions.Count));
                        return;
                    }

                    fetched = result;
                }
                catch (Exception exception)
                {
                    // Catalog Stop owns both sides of the operation: it marks
                    // the traversal stopped and aborts Catalog's private
                    // browser host. The resulting transport exception is an
                    // expected stop completion, not a failed sync.
                    if (Volatile.Read(ref _stopRequested) != 0)
                    {
                        Publish(Stopped(last, partitions.Count));
                        return;
                    }

                    Publish(Failed(last, partitions.Count, partition.Key, index, page, exception));
                    return;
                }

                fetchedAny = true;

                if (!IsCurrent(generation))
                {
                    return;
                }

                try
                {
                    last = await _store
                        .AppendPageAsync(partition, index, fetched, cancellationToken, refresh)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Publish(Failed(last, partitions.Count, partition.Key, index, page, exception));
                    return;
                }

                if (!IsCurrent(generation))
                {
                    return;
                }

                await RefreshPartitionsAsync(
                    last.StagingSnapshotId, generation, cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(generation))
                {
                    return;
                }

                Publish(new CatalogSyncProgress(
                    CatalogSyncState.Syncing, partition.Key, index, partitions.Count,
                    page, last.StagedRecords, 0, null, 0, null, _partitions));

                var partitionDone = !fetched.HasMore;
                if (!partitionDone && refresh && known is not null && fetched.Items.Count != 0)
                {
                    // Refresh overlap: one full page without a new identity ends
                    // the partition. Deletions never surface here by design.
                    var freshCount = 0;
                    foreach (var item in fetched.Items)
                    {
                        if (known.Add(item.SourceId + "\0" + item.TitleId))
                        {
                            freshCount++;
                        }
                    }

                    partitionDone = freshCount == 0;
                }

                if (partitionDone)
                {
                    // Record the finished partition at once: a Stop or crash
                    // after this point resumes at the next partition instead
                    // of re-entering a completed one.
                    try
                    {
                        last = await _store
                            .CompletePartitionAsync(index, partition.Key, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Publish(Failed(last, partitions.Count, partition.Key, index, page, exception));
                        return;
                    }

                    if (!IsCurrent(generation))
                    {
                        return;
                    }

                    break;
                }
            }
        }

        if (!IsCurrent(generation) || last is null)
        {
            return;
        }

        try
        {
            var result = await _store
                .ActivateAsync(last, cancellationToken, refresh)
                .ConfigureAwait(false);
            Publish(new CatalogSyncProgress(
                CatalogSyncState.Ready, null, partitions.Count, partitions.Count,
                last.Page, last.StagedRecords, result.UniqueTitles, DateTimeOffset.UtcNow,
                result.Warnings.Count + result.CleanupWarnings.Count, null, _partitions));
        }
        catch (Exception exception)
        {
            Publish(Failed(
                last, partitions.Count, last.PartitionKey, last.PartitionIndex, last.Page, exception));
        }
    }

    /// <summary>
    /// One page with bounded retries for typed provider backpressure
    /// (429/502/503): exponential base doubling with full jitter, same page,
    /// checkpoint unadvanced. Returns null when stopped or superseded while
    /// waiting. Anything else propagates at once.
    /// </summary>
    private async Task<CatalogSnapshotPage?> FetchWithBackoffAsync(
        CatalogSnapshotPartition partition,
        int page,
        int generation,
        bool refresh,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var wait = RetryBaseDelay < TimeSpan.Zero ? TimeSpan.Zero : RetryBaseDelay;
        for (var attempt = 0; attempt < MaxFetchAttempts; attempt++)
        {
            if (!IsCurrent(generation) || Volatile.Read(ref _stopRequested) != 0)
            {
                return null;
            }

            try
            {
                return await _source
                    .GetSnapshotPageAsync(partition, page, cancellationToken, refresh)
                    .ConfigureAwait(false);
            }
            // Typed backpressure only: v1 serves one source, so its contract
            // exception is the only throttle signal. Never message-sniffed;
            // a second source would bring its own typed signal here.
            catch (ComixContractException exception)
                when (exception.HttpStatus is 429 or 502 or 503)
            {
                lastError = exception;
                var capped = TimeSpan.FromMilliseconds(
                    Math.Min(wait.TotalMilliseconds * Math.Pow(2, attempt), 30_000));
                var jittered = TimeSpan.FromMilliseconds(
                    Random.Shared.NextDouble() * Math.Max(capped.TotalMilliseconds, 1));
                if (!await WaitInterruptiblyAsync(jittered, generation).ConfigureAwait(false))
                {
                    return null;
                }
            }
        }

        throw lastError ?? new CatalogSnapshotException("Snapshot page failed without a recorded error.");
    }

    /// <summary>
    /// Cooperative wait that wakes early on Stop or supersede. False means the
    /// caller must park without starting new provider work. A wait is never an
    /// in-flight request, so interrupting it breaks no transport contract.
    /// </summary>
    private async Task<bool> WaitInterruptiblyAsync(TimeSpan delay, int generation)
    {
        var remaining = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        while (remaining > TimeSpan.Zero)
        {
            if (!IsCurrent(generation) || Volatile.Read(ref _stopRequested) != 0)
            {
                return false;
            }

            var slice = remaining > TimeSpan.FromMilliseconds(100)
                ? TimeSpan.FromMilliseconds(100)
                : remaining;
            await Task.Delay(slice).ConfigureAwait(false);
            remaining -= slice;
        }

        return IsCurrent(generation) && Volatile.Read(ref _stopRequested) == 0;
    }

    /// <summary>
    /// Reloads the committed per-partition table for progress display. Best
    /// effort: a metadata read never fails the traversal itself.
    /// </summary>
    private async Task RefreshPartitionsAsync(
        string? stagingSnapshotId,
        int generation,
        CancellationToken cancellationToken)
    {
        if (stagingSnapshotId is null || !IsCurrent(generation))
        {
            return;
        }

        try
        {
            var table = await _store
                .GetPartitionProgressAsync(stagingSnapshotId, cancellationToken)
                .ConfigureAwait(false);
            if (IsCurrent(generation))
            {
                _partitions = table;
            }
        }
        catch (Exception)
        {
            // Progress metadata stays at its last good snapshot.
        }
    }

    private bool IsCurrent(int generation) =>
        Volatile.Read(ref _generation) == generation;

    private void Publish(CatalogSyncProgress progress)
    {
        lock (_gate)
        {
            _current = progress;
        }

        StateChanged?.Invoke(this, progress);
    }

    private CatalogSyncProgress Stopped(
        CatalogSnapshotCheckpoint? last, int partitionCount) =>
        new(CatalogSyncState.Stopped,
            last?.PartitionKey, last?.PartitionIndex ?? 0, partitionCount,
            last?.Page ?? 0, last?.StagedRecords ?? 0, 0, null, 0, null, _partitions);

    private CatalogSyncProgress Failed(
        CatalogSnapshotCheckpoint? last,
        int partitionCount,
        string? attemptedPartitionKey,
        int partitionIndex,
        int page,
        Exception exception) =>
        new(CatalogSyncState.Error,
            attemptedPartitionKey ?? last?.PartitionKey, partitionIndex, partitionCount,
            page, last?.StagedRecords ?? 0, 0, null, 0,
            exception.GetBaseException().Message, _partitions);
}
