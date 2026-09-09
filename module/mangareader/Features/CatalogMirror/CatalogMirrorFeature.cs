using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

// Thin coordinator combining immutable query and sync state and forwarding
// intents to their owners. It never touches the filesystem, network,
// provider or queue directly, and holds no parsing, filtering, download or
// WPF logic. All public async methods are safe to call from the UI thread;
// heavy work runs off it, and the view marshals StateChanged to its
// Dispatcher before touching controls.
public sealed class CatalogMirrorFeature : IDisposable
{
    private readonly CatalogMirrorLoadFeature _load;
    private readonly CatalogMirrorSyncFeature? _sync;
    private readonly CatalogGenreSyncFeature? _genreSync;
    private readonly CatalogMirrorDetailFeature _detail;
    private readonly object _gate = new();
    private CatalogMirrorQueryRequest _request = CatalogMirrorQueryRequest.Default;
    private IReadOnlyList<string> _lastWarnings = [];
    private DateTimeOffset? _lastDeltaUtc;
    private CatalogMirrorState _current = new(
        CatalogMirrorQueryRequest.Default,
        null,
        new CatalogSyncProgress(
            CatalogSyncState.Empty, null, 0, 0, 0, 0, 0, null, 0, null, []),
        null,
        false);
    private bool _disposed;

    public CatalogMirrorFeature(
        CatalogMirrorLoadFeature load,
        CatalogMirrorSyncFeature? sync,
        CatalogMirrorDetailFeature detail)
        : this(load, sync, null, detail)
    {
    }

    public CatalogMirrorFeature(
        CatalogMirrorLoadFeature load,
        CatalogMirrorSyncFeature? sync,
        CatalogGenreSyncFeature? genreSync,
        CatalogMirrorDetailFeature detail)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _sync = sync;
        _genreSync = genreSync;
        _detail = detail ?? throw new ArgumentNullException(nameof(detail));
        if (_sync is not null)
        {
            _sync.StateChanged += OnSyncProgress;
        }

        if (_genreSync is not null)
        {
            _genreSync.StateChanged += OnGenreSyncProgress;
        }

        _detail.StateChanged += OnDetailState;
    }

    public CatalogMirrorState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<CatalogMirrorState>? StateChanged;

    /// <summary>Snapshot-driven filter options for the panel, or empty.</summary>
    public IReadOnlyList<string> AvailableTypes    {
        get
        {
            lock (_gate)
            {
                return _load.AvailableTypes;
            }
        }
    }

    public IReadOnlyList<string> AvailableStatuses
    {
        get
        {
            lock (_gate)
            {
                return _load.AvailableStatuses;
            }
        }
    }

    public IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            lock (_gate)
            {
                return _load.AvailableLanguages;
            }
        }
    }

    public IReadOnlyList<CatalogGenreOption> AvailableGenres
    {
        get { lock (_gate) return _load.AvailableGenres; }
    }

    public bool IsGenreEnrichmentComplete
    {
        get { lock (_gate) return _load.IsGenreEnrichmentComplete; }
    }
    /// <summary>Warning strings of the loaded manifest, for the warnings dialog.</summary>
    public IReadOnlyList<string> LastWarnings
    {
        get
        {
            lock (_gate)
            {
                return _lastWarnings;
            }
        }
    }

    /// <summary>Refresh watermark of the loaded manifest, if any refresh ran.</summary>
    public DateTimeOffset? LastDeltaUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastDeltaUtc;
            }
        }
    }

    /// <summary>
    /// Loads the active snapshot without any browser and publishes the default
    /// local result. Only the manifest crosses here; titles stay in the
    /// database until the queried page materializes them. Stays empty when no
    /// sync ever activated.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_sync is not null)
        {
            await _sync.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var manifest = await _load.ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return;
        }

        CatalogMirrorState published;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _request = CatalogMirrorQueryRequest.Default;
            var restoredSync = _sync?.Current;
            var sync = restoredSync is null || restoredSync.State == CatalogSyncState.Empty
                ? new CatalogSyncProgress(
                    CatalogSyncState.Ready, null, 0, 0, 0, 0,
                    manifest.UniqueTitles, manifest.CapturedAtUtc,
                    manifest.Warnings.Count, null, [])
                : restoredSync with
                {
                    UniqueTitles = manifest.UniqueTitles,
                    LastSyncedUtc = manifest.CapturedAtUtc,
                    WarningCount = manifest.Warnings.Count,
                };
            published = new CatalogMirrorState(
                _request,
                null,
                sync,
                null,
                false,
                _genreSync?.Current);
            _current = published;
            _lastWarnings = manifest.Warnings;
            _lastDeltaUtc = manifest.LastDeltaUtc;
        }

        Publish(published);
    }

    /// <summary>
    /// Runs one query against the database generation selected by Load Catalog.
    /// Search never starts sync and never silently chooses a staging generation.
    /// </summary>
    public async Task QueryAsync(CatalogMirrorQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await _load.QueryAsync(request, cancellationToken).ConfigureAwait(false);
        if (loaded is null) return;

        CatalogMirrorState published;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _request = request;
            published = new CatalogMirrorState(
                request, loaded.Page, _current.Sync, _current.Detail, loaded.IsPartial,
                _current.GenreSync);
            _current = published;
        }

        Publish(published);
    }

    /// <summary>
    /// Explicit DB-to-display action. It selects active data, or first-sync
    /// staging data when no active snapshot exists, and never invokes sync.
    /// </summary>
    public async Task<bool> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        CatalogMirrorQueryRequest request;
        lock (_gate)
        {
            if (_disposed) return false;
            request = _request;
        }

        var loaded = await _load.LoadAsync(request, cancellationToken).ConfigureAwait(false);
        if (loaded is null) return false;

        CatalogMirrorState published;
        lock (_gate)
        {
            if (_disposed) return false;
            published = new CatalogMirrorState(
                request, loaded.Page, _current.Sync, _current.Detail, loaded.IsPartial,
                _current.GenreSync);
            _current = published;
            if (loaded.Manifest is not null)
            {
                _lastWarnings = loaded.Manifest.Warnings;
                _lastDeltaUtc = loaded.Manifest.LastDeltaUtc;
            }
        }

        Publish(published);
        return true;
    }

    public Task StartSyncAsync(CancellationToken cancellationToken)
    {
        if (_sync is null)
        {
            PublishError("No snapshot source is available.");
            return Task.CompletedTask;
        }

        return _sync.StartAsync(cancellationToken);
    }

    public Task ResumeSyncAsync(CancellationToken cancellationToken)
    {
        if (_sync is null)
        {
            PublishError("No snapshot source is available.");
            return Task.CompletedTask;
        }

        return _sync.ResumeAsync(cancellationToken);
    }

    /// <summary>Incremental refresh through the sync owner (no-op error without one).</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_sync is null)
        {
            PublishError("No snapshot source is available.");
            return Task.CompletedTask;
        }

        return _sync.RefreshAsync(cancellationToken);
    }

    public void RequestStopSync() => _sync?.RequestStop();

    public Task StartGenreSyncAsync(CancellationToken cancellationToken)
    {
        if (_genreSync is null)
        {
            PublishError("No genre snapshot source is available.");
            return Task.CompletedTask;
        }

        bool waitForBuilding;
        lock (_gate)
        {
            waitForBuilding = _current.Sync.State == CatalogSyncState.Syncing;
        }

        return _genreSync.StartAsync(waitForBuilding, cancellationToken);
    }

    public Task ResumeGenreSyncAsync(CancellationToken cancellationToken)
    {
        if (_genreSync is null) return Task.CompletedTask;
        bool waitForBuilding;
        lock (_gate)
        {
            waitForBuilding = _current.Sync.State == CatalogSyncState.Syncing;
        }

        return _genreSync.ResumeAsync(waitForBuilding, cancellationToken);
    }

    public void RequestStopGenreSync() => _genreSync?.RequestStop();

    /// <summary>Opens one title through the detail owner.</summary>
    public Task OpenTitleAsync(CatalogSnapshotItem title, CancellationToken cancellationToken) =>
        _detail.OpenTitleAsync(title, cancellationToken);

    /// <summary>Switches the active source group through the detail owner.</summary>
    public Task SelectGroupAsync(string groupId, CancellationToken cancellationToken) =>
        _detail.SelectGroupAsync(groupId, cancellationToken);

    public void SetChapterSelected(string chapterId, bool selected) =>
        _detail.SetChapterSelected(chapterId, selected);

    public void SelectAllChapters(bool selected) => _detail.SelectAllChapters(selected);

    /// <summary>Closes the detail; the view restores the exact local result.</summary>
    public void CloseDetail() => _detail.Close();

    /// <summary>Builds the immutable handoff for the current selection, if any.</summary>
    public CatalogQueueHandoffRequest? TryBuildHandoff(string targetFolder) =>
        _detail.TryBuildHandoff(targetFolder);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_sync is not null)
        {
            _sync.StateChanged -= OnSyncProgress;
        }

        if (_genreSync is not null)
        {
            _genreSync.StateChanged -= OnGenreSyncProgress;
        }

        _detail.StateChanged -= OnDetailState;
    }

    private void OnSyncProgress(object? sender, CatalogSyncProgress progress)
    {
        try
        {
            CatalogMirrorState published;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                published = _current with { Sync = progress };
                _current = published;
            }

            Publish(published);
        }
        catch (Exception exception)
        {
            PublishError(exception.GetBaseException().Message);
        }
    }

    private void OnDetailState(object? sender, CatalogDetailState? detail)
    {
        CatalogMirrorState published;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            published = _current with { Detail = detail };
            _current = published;
        }

        Publish(published);
    }

    private void OnGenreSyncProgress(object? sender, CatalogGenreSyncProgress progress)
    {
        CatalogMirrorState published;
        lock (_gate)
        {
            if (_disposed) return;
            published = _current with { GenreSync = progress };
            _current = published;
        }

        Publish(published);
    }

    private void PublishError(string message)
    {
        CatalogMirrorState published;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var sync = _current.Sync;
            published = _current with
            {
                Sync = sync with
                {
                    State = CatalogSyncState.Error,
                    ErrorMessage = message,
                },
            };
            _current = published;
        }

        Publish(published);
    }

    /// <summary>
    /// Notifies subscribers. Always called without holding the gate, so a
    /// subscriber can never deadlock against a feature call.
    /// </summary>
    private void Publish(CatalogMirrorState state)
    {
        StateChanged?.Invoke(this, state);
    }
}
