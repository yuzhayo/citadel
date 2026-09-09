using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

/// <summary>
/// Background genre enrichment driven only by title rows already committed to
/// the Catalog database. One title detail request produces the genre relations
/// for that same title; no browse-by-genre traversal exists here.
/// </summary>
public sealed class CatalogGenreSyncFeature(
    IMangaSourceDirectory sources,
    CatalogGenreStore store)
{
    private readonly IMangaSourceDirectory _sources =
        sources ?? throw new ArgumentNullException(nameof(sources));
    private readonly CatalogGenreStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private int _stopRequested;
    private int _runGeneration;
    private CatalogGenreSyncProgress _current = new(
        CatalogGenreSyncState.ReadyToSync, null, 0, 0, 0, null);

    public TimeSpan PoliteDelay { get; set; } = TimeSpan.FromSeconds(1);

    public CatalogGenreSyncProgress Current => _current;

    public event EventHandler<CatalogGenreSyncProgress>? StateChanged;

    public Task StartAsync(bool waitForBuildingGeneration, CancellationToken cancellationToken) =>
        RunAsync(
            fresh: true,
            waitForBuildingGeneration,
            Interlocked.Increment(ref _runGeneration),
            cancellationToken);

    public Task ResumeAsync(bool waitForBuildingGeneration, CancellationToken cancellationToken) =>
        RunAsync(
            fresh: false,
            waitForBuildingGeneration,
            Interlocked.Increment(ref _runGeneration),
            cancellationToken);

    public void RequestStop()
    {
        Volatile.Write(ref _stopRequested, 1);
        if (_current.State == CatalogGenreSyncState.Syncing)
        {
            Publish(_current with { State = CatalogGenreSyncState.Stopping });
        }
    }

    private async Task RunAsync(
        bool fresh,
        bool waitForBuildingGeneration,
        int runGeneration,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopRequested, 0);
        string? generationId = null;
        var prepared = false;

        while (IsCurrent(runGeneration))
        {
            if (Stopped(runGeneration, generationId)) return;

            generationId ??= _store.ResolveGeneration(waitForBuildingGeneration);
            if (generationId is null)
            {
                Publish(new CatalogGenreSyncProgress(
                    CatalogGenreSyncState.Syncing,
                    "Waiting for committed catalog titles…",
                    0, 0, 0, null));
                if (!await WaitAsync(runGeneration, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
                {
                    PublishStopped(null);
                    return;
                }
                continue;
            }

            if (!prepared)
            {
                try
                {
                    _store.Prepare(generationId, fresh);
                    prepared = true;
                }
                catch (Exception exception)
                {
                    Publish(Failed(exception));
                    return;
                }
            }

            // Capture the non-null owner for this iteration. The outer value
            // may intentionally be reset after an in-flight generation swap.
            var workGenerationId = generationId;
            var counts = _store.ReadCounts(workGenerationId);
            var title = _store.ReadNext(workGenerationId);
            if (title is null)
            {
                if (string.Equals(counts.GenerationState, "ready", StringComparison.Ordinal))
                {
                    _store.SetState(workGenerationId, "ready");
                    Publish(new CatalogGenreSyncProgress(
                        CatalogGenreSyncState.Ready,
                        null,
                        counts.ProcessedTitles,
                        counts.AvailableTitles,
                        counts.TaggedTitles,
                        null));
                    return;
                }

                // The title sync still owns this building generation. Wait for
                // its next committed page; never manufacture a genre request.
                Publish(new CatalogGenreSyncProgress(
                    CatalogGenreSyncState.Syncing,
                    "Waiting for more committed titles…",
                    counts.ProcessedTitles,
                    counts.AvailableTitles,
                    counts.TaggedTitles,
                    null));
                if (!await WaitAsync(runGeneration, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
                {
                    PublishStopped(workGenerationId);
                    return;
                }
                continue;
            }

            var source = _sources.FindSource(title.SourceId);
            if (source is null)
            {
                var exception = new CatalogSnapshotException(
                    $"Catalog source '{title.SourceId}' is not available for genre enrichment.");
                _store.MarkTitleError(title, exception);
                Publish(Failed(exception) with
                {
                    TitleName = title.Title,
                    ProcessedTitles = counts.ProcessedTitles,
                    AvailableTitles = counts.AvailableTitles,
                    TaggedTitles = counts.TaggedTitles,
                });
                return;
            }

            Publish(new CatalogGenreSyncProgress(
                CatalogGenreSyncState.Syncing,
                title.Title,
                counts.ProcessedTitles,
                counts.AvailableTitles,
                counts.TaggedTitles,
                null));

            try
            {
                var identity = new RemoteTitleIdentity(
                    title.SourceId,
                    title.TitleId,
                    title.TitleHid,
                    Slug: string.Empty);
                var detail = await source
                    .GetTitleAsync(identity, cancellationToken)
                    .ConfigureAwait(false);

                if (!_store.SaveTitleGenres(title, detail.Genres))
                {
                    // A fresh Catalog sync replaced the generation while this
                    // request was in flight. Resolve the current owner and keep
                    // the response out of the wrong generation.
                    generationId = null;
                    prepared = false;
                    fresh = false;
                    waitForBuildingGeneration = false;
                    continue;
                }
            }
            catch (Exception exception)
            {
                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    PublishStopped(workGenerationId);
                    return;
                }

                _store.MarkTitleError(title, exception);
                var failedCounts = _store.ReadCounts(workGenerationId);
                Publish(Failed(exception) with
                {
                    TitleName = title.Title,
                    ProcessedTitles = failedCounts.ProcessedTitles,
                    AvailableTitles = failedCounts.AvailableTitles,
                    TaggedTitles = failedCounts.TaggedTitles,
                });
                return;
            }

            var updated = _store.ReadCounts(workGenerationId);
            Publish(new CatalogGenreSyncProgress(
                CatalogGenreSyncState.Syncing,
                title.Title,
                updated.ProcessedTitles,
                updated.AvailableTitles,
                updated.TaggedTitles,
                null));

            if (!await WaitAsync(runGeneration, PoliteDelay).ConfigureAwait(false))
            {
                PublishStopped(workGenerationId);
                return;
            }
        }
    }

    private bool Stopped(int generation, string? generationId)
    {
        if (IsCurrent(generation) && Volatile.Read(ref _stopRequested) == 0) return false;
        PublishStopped(generationId);
        return true;
    }

    private void PublishStopped(string? generationId)
    {
        CatalogGenreWorkCounts? counts = null;
        if (!string.IsNullOrWhiteSpace(generationId))
        {
            try
            {
                _store.SetState(generationId, "stopped");
                counts = _store.ReadCounts(generationId);
            }
            catch (Exception)
            {
                // A replaced generation is already stopped by ownership loss.
            }
        }

        Publish(new CatalogGenreSyncProgress(
            CatalogGenreSyncState.Stopped,
            null,
            counts?.ProcessedTitles ?? _current.ProcessedTitles,
            counts?.AvailableTitles ?? _current.AvailableTitles,
            counts?.TaggedTitles ?? _current.TaggedTitles,
            null));
    }

    private async Task<bool> WaitAsync(int generation, TimeSpan delay)
    {
        var remaining = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        while (remaining > TimeSpan.Zero)
        {
            if (!IsCurrent(generation) || Volatile.Read(ref _stopRequested) != 0) return false;
            var slice = remaining > TimeSpan.FromMilliseconds(100)
                ? TimeSpan.FromMilliseconds(100)
                : remaining;
            await Task.Delay(slice).ConfigureAwait(false);
            remaining -= slice;
        }

        return IsCurrent(generation) && Volatile.Read(ref _stopRequested) == 0;
    }

    private bool IsCurrent(int generation) => Volatile.Read(ref _runGeneration) == generation;

    private CatalogGenreSyncProgress Failed(Exception exception) => new(
        CatalogGenreSyncState.Error,
        null,
        _current.ProcessedTitles,
        _current.AvailableTitles,
        _current.TaggedTitles,
        exception.GetBaseException().Message);

    private void Publish(CatalogGenreSyncProgress progress)
    {
        _current = progress;
        StateChanged?.Invoke(this, progress);
    }
}
