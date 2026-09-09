using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Module.Mangareader.Features.CatalogMirror;

/// <summary>
/// Database-to-display path for Catalog. It never starts sync, calls a source,
/// or reacts to sync progress. Load chooses the active SQLite snapshot (or the
/// first-sync staging generation); later queries only read that chosen database
/// generation until Load is pressed again.
/// </summary>
public sealed class CatalogMirrorLoadFeature
{
    private readonly CatalogSnapshotStore _store;
    private readonly object _gate = new();
    private CatalogMirrorQuery? _index;
    private bool _isPartial;
    private long _generation;

    public CatalogMirrorLoadFeature(CatalogSnapshotStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<string> AvailableTypes
    {
        get { lock (_gate) return _index?.DistinctTypes ?? []; }
    }

    public IReadOnlyList<string> AvailableStatuses
    {
        get { lock (_gate) return _index?.DistinctStatuses ?? []; }
    }

    public IReadOnlyList<string> AvailableLanguages
    {
        get { lock (_gate) return _index?.DistinctLanguages ?? []; }
    }

    public IReadOnlyList<CatalogGenreOption> AvailableGenres
    {
        get { lock (_gate) return _index?.DistinctGenres ?? []; }
    }

    public bool IsGenreEnrichmentComplete
    {
        get { lock (_gate) return _index?.IsGenreEnrichmentComplete == true; }
    }

    public Task<CatalogSnapshotManifest?> ReadManifestAsync(
        CancellationToken cancellationToken = default) =>
        _store.TryLoadManifestAsync(cancellationToken);

    public async Task<CatalogMirrorLoadResult?> LoadAsync(
        CatalogMirrorQueryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var manifest = await _store.TryLoadManifestAsync(cancellationToken).ConfigureAwait(false);
        string snapshotId;
        var partial = false;
        if (manifest is not null)
        {
            snapshotId = manifest.ActiveSnapshotId;
        }
        else
        {
            var checkpoint = await _store.LoadCheckpointAsync(cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                return null;
            }

            snapshotId = checkpoint.StagingSnapshotId;
            partial = true;
        }

        var generation = Interlocked.Increment(ref _generation);
        var index = new CatalogMirrorQuery(_store.Database, snapshotId);
        var page = await Task.Run(() => index.Execute(request), cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return null;
            }

            _index = index;
            _isPartial = partial;
        }

        return new CatalogMirrorLoadResult(page, partial, manifest);
    }

    public async Task<CatalogMirrorLoadResult?> QueryAsync(
        CatalogMirrorQueryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        CatalogMirrorQuery? index;
        bool partial;
        long generation;
        lock (_gate)
        {
            index = _index;
            partial = _isPartial;
            generation = Interlocked.Increment(ref _generation);
        }

        if (index is null)
        {
            return null;
        }

        var indexGeneration = index.BeginQuery();
        var page = await Task.Run(() => index.Execute(request), cancellationToken).ConfigureAwait(false);
        if (!index.IsLatest(indexGeneration))
        {
            return null;
        }

        lock (_gate)
        {
            if (generation != Volatile.Read(ref _generation) || !ReferenceEquals(_index, index))
            {
                return null;
            }
        }

        return new CatalogMirrorLoadResult(page, partial, null);
    }
}

public sealed record CatalogMirrorLoadResult(
    CatalogMirrorResultPage Page,
    bool IsPartial,
    CatalogSnapshotManifest? Manifest);
