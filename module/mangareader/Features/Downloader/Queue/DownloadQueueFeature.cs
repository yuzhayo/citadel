using System.IO;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>
/// What one queue command did. The two skip counts are kept apart because they mean
/// different things to the caller: a published chapter is one the user may
/// explicitly choose to replace, while a job that has not finished is one that must
/// never be duplicated whatever the user confirmed.
/// </summary>
public sealed record QueueAddResult(
    int Queued,
    int SkippedAlreadyPublished,
    int SkippedAlreadyQueued,
    string? Blocked);

/// <summary>
/// The sole owner of download job state and effects. It persists every
/// transition before the effect it authorizes, keeps queue order stable, and
/// never lets a screen hold mutable job collections.
///
/// Threading: state is guarded by one lock; the scheduler runs off the UI
/// thread. Marshalling progress to the UI is the presentation adapter's job.
/// </summary>
public sealed class DownloadQueueFeature : IDisposable
{
    internal const int JobConcurrency = 4;
    // Each independent Comix manifest starts its own browser/proxy bootstrap.
    // Keep that lane below the page lane's combined load so it does not create
    // a startup CPU spike while downloads are already progressing.
    internal const int ManifestConcurrency = 8;

    private readonly LibraryRootContext _root;
    private readonly MangaSourceRegistry _sources;
    private readonly DownloaderPyHostClient _browser;
    private readonly ProxyHttpTransport? _httpTransport;
    private readonly DownloadQueueStore _store;
    private readonly DownloadSourceIndex _index;
    private readonly object _gate = new();
    private List<DownloadJobRecord> _jobs = [];
    private readonly QueueScheduler _manifestScheduler;
    private readonly QueueScheduler _downloadScheduler;
    private readonly HashSet<string> _manualDownloadsAfterManifest = new(StringComparer.Ordinal);
    private readonly QueueSharedSessionAdapter _shared;
    private readonly HashSet<string> _removing = new(StringComparer.Ordinal);
    private int _disposed;

    public DownloadQueueFeature(
        LibraryRootContext root,
        MangaSourceRegistry sources,
        DownloaderPyHostClient browser,
        DownloadQueueStore store,
        DownloadSourceIndex index)
        : this(root, sources, browser, store, index, null)
    {
    }

    internal DownloadQueueFeature(
        LibraryRootContext root,
        MangaSourceRegistry sources,
        DownloaderPyHostClient browser,
        DownloadQueueStore store,
        DownloadSourceIndex index,
        ProxyHttpTransport? httpTransport)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _httpTransport = httpTransport;
        _manifestScheduler = new QueueScheduler(
            _gate,
            Snapshot,
            job => job.State is DownloadJobState.Queued
                or DownloadJobState.RefreshingManifest
                or DownloadJobState.ResolvingAlternates,
            ManifestConcurrency,
            RunManifestClaimedAsync);
        _downloadScheduler = new QueueScheduler(
            _gate,
            Snapshot,
            job => job.State == DownloadJobState.ManifestReady,
            JobConcurrency,
            RunDownloadClaimedAsync);
        _manifestScheduler.Settled += (_, job) =>
        {
            var startManualDownload = false;
            lock (_gate) startManualDownload = _manualDownloadsAfterManifest.Remove(job.JobId);
            if (startManualDownload && Snapshot().Any(item => item.JobId == job.JobId
                    && item.State == DownloadJobState.ManifestReady))
            {
                _downloadScheduler.MarkManual(job.JobId);
            }
            _downloadScheduler.Wake();
        };
        _downloadScheduler.Settled += (_, _) => _manifestScheduler.Wake();
        _shared = new QueueSharedSessionAdapter(browser, reason => StopAll(reason));
        LoadPersisted();
    }

    /// <summary>
    /// The one queue change signal named in the route surface. It covers the
    /// Catalog badge summary and Download List row state together, because both
    /// are projections of the same durable order — there is deliberately no
    /// second event for the same owner.
    /// </summary>
    public event EventHandler? QueueSummaryChanged;

    public IReadOnlyList<DownloadJobRecord> Snapshot()
    {
        lock (_gate)
        {
            return [.. _jobs];
        }
    }

    public QueueSummary Summary()
    {
        lock (_gate)
        {
            var resolving = _jobs.Count(job => job.State is DownloadJobState.Resolving
                or DownloadJobState.RefreshingManifest
                or DownloadJobState.ResolvingAlternates);
            var ready = _jobs.Count(job => job.State == DownloadJobState.ManifestReady);
            var downloading = _jobs.Count(job => job.State is DownloadJobState.Downloading
                or DownloadJobState.Recovering or DownloadJobState.Decoding
                or DownloadJobState.Validating or DownloadJobState.Publishing);
            return new QueueSummary(
                _jobs.Count,
                resolving + ready + downloading,
                _jobs.Count(job => job.State is DownloadJobState.Paused or DownloadJobState.Pausing),
                _jobs.Count(job => job.State == DownloadJobState.Failed),
                resolving,
                ready,
                downloading);
        }
    }

    /// <summary>
    /// Queues the selected chapters of one group into one confirmed target
    /// folder. The root is snapshotted here, so a later Library change cannot
    /// redirect an active job.
    /// </summary>
    /// <param name="allowPublishedReplacement">
    /// The caller's recorded confirmation that an already published chapter may
    /// be downloaded again. It defaults to false, so a caller that never asked
    /// keeps the skip and can never overwrite a library file silently. When
    /// true the bypass reaches exactly one check —
    /// <see cref="DownloadSourceIndex.IsPublishedOnDisk"/>. It never reaches the
    /// duplicate guard for a job that has not finished, which applies whatever the
    /// caller confirmed. Identity, target filename, provenance and the publisher's
    /// own collision policy are untouched, so the same source/group lands on the
    /// same target for an atomic replacement while a different source/group still
    /// gets its own identity and its own file.
    /// </param>
    public QueueAddResult QueueChapters(
        RemoteTitleSummary title,
        RemoteSourceGroup group,
        IReadOnlyList<RemoteChapterSummary> chapters,
        string folderName,
        bool allowPublishedReplacement = false)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        var root = _root.CurrentRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return new QueueAddResult(
                0,
                0,
                0,
                "Library root belum diatur; pilih folder Library lebih dulu.");
        }

        var sanitizedFolder = SanitizeFolder(folderName);
        _index.ConfirmMapping(new SourceTitleMapping(
            title.Identity.SourceId,
            title.Identity.TitleId,
            title.Identity.TitleHid,
            sanitizedFolder,
            DateTimeOffset.UtcNow));

        // A read-only pre-check, so a batch that adds nothing costs no durable write,
        // no change signal and no scheduler start. It is deliberately not the
        // authority: the same question is asked again inside the commit transaction
        // below, which is the only place two concurrent admissions are serialized
        // against each other.
        HashSet<string> reserved;
        lock (_gate)
        {
            reserved = ReservedIdentityKeys(_jobs);
        }

        var skippedPublished = 0;
        var skippedQueued = 0;
        var candidates = new List<DownloadJobRecord>();
        // Provider chapter lists are normally newest-first. Queue the selected
        // batch in reading order so the stable FIFO scheduler starts at the
        // smallest chapter without changing any job already in the queue.
        foreach (var chapter in chapters.OrderBy(
                     chapter => NormalizeNumber(chapter.Identity.ChapterNumber),
                     NaturalStringComparer.OrdinalIgnoreCase))
        {
            var identity = new DownloadJobIdentity(
                chapter.Identity.SourceId,
                title.Identity.TitleId,
                title.Identity.TitleHid,
                chapter.Identity.ChapterId,
                group.Identity.GroupId);

            if (reserved.Contains(identity.Key))
            {
                skippedQueued++;
                continue;
            }

            // On disk, not merely on record: a CBZ the user deleted leaves its index
            // entry behind, and skipping on the entry alone would refuse a download
            // the Library no longer has, without ever showing a confirmation.
            if (!allowPublishedReplacement && _index.IsPublishedOnDisk(identity, root, sanitizedFolder))
            {
                skippedPublished++;
                continue;
            }

            candidates.Add(new DownloadJobRecord
            {
                JobId = Guid.NewGuid().ToString("N"),
                Identity = identity,
                TitleDisplayName = title.DisplayName,
                ChapterDisplayName = chapter.DisplayName,
                GroupDisplayName = group.DisplayName,
                ChapterNumber = chapter.Identity.ChapterNumber,
                Target = new DownloadTarget(root, sanitizedFolder, BuildFileName(chapter, group)),
                State = DownloadJobState.Queued,
                QueuedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
        }

        if (candidates.Count == 0)
        {
            return new QueueAddResult(0, skippedPublished, skippedQueued, null);
        }

        var appended = new List<DownloadJobRecord>();
        Commit(jobs =>
        {
            // Decision and append in one transaction. Reserving each identity in the
            // same set that reports the live ones is what makes this safe under two
            // concurrent callers, and it also keeps one batch from repeating itself.
            var taken = ReservedIdentityKeys(jobs);
            foreach (var candidate in candidates)
            {
                if (!taken.Add(candidate.Identity.Key))
                {
                    skippedQueued++;
                    continue;
                }

                appended.Add(candidate);
            }

            // Appended at the end: queue order is stable and a retry never
            // reorders other jobs.
            jobs.AddRange(appended);
        });

        // Admission only records the user's selection. A queue is deliberately
        // manual: browser/proxy work begins only from Start / Resume or a row
        // Start command, never merely because chapters were added.
        return new QueueAddResult(appended.Count, skippedPublished, skippedQueued, null);
    }

    /// <summary>
    /// The one definition of an identity that is already spoken for. Completed work
    /// may be queued again after explicit replacement confirmation; unfinished and
    /// failed work keeps its existing row and must be resumed instead of duplicated.
    /// </summary>
    private static HashSet<string> ReservedIdentityKeys(IEnumerable<DownloadJobRecord> jobs) =>
        jobs.Where(job => job.State != DownloadJobState.Completed)
            .Select(job => job.Identity.Key)
            .ToHashSet(StringComparer.Ordinal);

    private bool IsActive(string jobId) =>
        _manifestScheduler.IsActive(jobId) || _downloadScheduler.IsActive(jobId);

    private Task CancelActive(IEnumerable<string> jobIds) =>
        Task.WhenAll(
            _manifestScheduler.Cancel(jobIds),
            _downloadScheduler.Cancel(jobIds));

    private void MarkManual(string jobId, DownloadJobState state)
    {
        if (state == DownloadJobState.Queued)
        {
            _manualDownloadsAfterManifest.Add(jobId);
            _manifestScheduler.MarkManual(jobId);
        }
        else if (state == DownloadJobState.ManifestReady)
        {
            _downloadScheduler.MarkManual(jobId);
        }
    }

    public void Pause(string jobId) => Stop(jobId);

    public void Stop(string jobId) => StopMany([jobId], null);
    public void StopAll() => StopAll(null);
    private void StopAll(string? reason)
    {
        lock (_gate) StopMany(_jobs.Select(job => job.JobId).ToArray(), reason);
    }

    private Task StopMany(IReadOnlyCollection<string> ids, string? reason)
    {
        var targets = ids.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            _manualDownloadsAfterManifest.ExceptWith(targets);
            Commit(jobs =>
            {
                for (var i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    if (!targets.Contains(job.JobId) || job.State == DownloadJobState.Completed) continue;
                    jobs[i] = job with
                    {
                        State = IsActive(job.JobId) ? DownloadJobState.Pausing : DownloadJobState.Paused,
                        Warning = reason ?? job.Warning,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    };
                }
            });
            return CancelActive(targets);
        }
    }

    public void Start(string jobId)
    {
        lock (_gate)
        {
            if (_removing.Contains(jobId) || IsActive(jobId)) return;
            Transition(jobId, job => job.State is DownloadJobState.Paused or DownloadJobState.Failed or DownloadJobState.Queued
                ? job with
                {
                    State = HasValidManifest(job) ? DownloadJobState.ManifestReady : DownloadJobState.Queued,
                    Warning = null,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                }
                : job);
            var state = _jobs.FirstOrDefault(job => job.JobId == jobId)?.State;
            if (state is DownloadJobState.Queued or DownloadJobState.ManifestReady)
                MarkManual(jobId, state.Value);
        }
        EnsureStarted();
    }

    public void Retry(string jobId) => Start(jobId);

    public static string TitleGroupKey(DownloadJobRecord job) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { job.Identity.SourceId, job.Identity.TitleId, job.Identity.TitleHid });

    public void StopGroup(string groupKey)
    {
        lock (_gate) StopMany(_jobs.Where(job => TitleGroupKey(job) == groupKey).Select(job => job.JobId).ToArray(), null);
    }

    public void ResumeGroup(string groupKey) => ResumeMatching(job => TitleGroupKey(job) == groupKey);

    private void ResumeMatching(Func<DownloadJobRecord, bool> matches)
    {
        Commit(jobs =>
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (!matches(job) || _removing.Contains(job.JobId) || IsActive(job.JobId)
                    || job.State is not (DownloadJobState.Queued
                        or DownloadJobState.ManifestReady
                        or DownloadJobState.Paused
                        or DownloadJobState.Failed)) continue;
                jobs[i] = job with
                {
                    State = HasValidManifest(job) ? DownloadJobState.ManifestReady : DownloadJobState.Queued,
                    Warning = null,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            }
        });
        EnsureStarted();
    }

    public void Resume(string jobId)
    {
        ResumeMatching(job => job.JobId == jobId);
    }

    /// <summary>
    /// Resumes every parked or failed job in one durable queue mutation. Existing
    /// JobIds and staging remain intact, so this never creates duplicate rows.
    /// </summary>
    public void ResumeAll()
    {
        var changed = false;
        Commit(jobs =>
        {
            for (var index = 0; index < jobs.Count; index++)
            {
                var job = jobs[index];
                if (job.State is not (DownloadJobState.Queued
                    or DownloadJobState.ManifestReady
                    or DownloadJobState.Paused
                    or DownloadJobState.Failed)
                    || _removing.Contains(job.JobId) || IsActive(job.JobId)) continue;

                jobs[index] = job with
                {
                    State = HasValidManifest(job) ? DownloadJobState.ManifestReady : DownloadJobState.Queued,
                    Warning = null,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
                changed = true;
            }
        });

        if (changed) EnsureStarted();
    }

    /// <summary>
    /// Applies a user-confirmed whole-chapter group fallback. The replacement
    /// gets the alternate group's identity and filename; the old staging stays
    /// recoverable until the replacement succeeds or the job is removed.
    /// </summary>
    public void ConfirmSourceFallback(string jobId, SourceFallbackCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Transition(jobId, job =>
        {
            if (job.State != DownloadJobState.AwaitingSourceFallback) return job;
            return job with
            {
                Identity = job.Identity with
                {
                    ChapterId = candidate.ChapterId,
                    GroupId = candidate.GroupId,
                },
                GroupDisplayName = candidate.GroupName,
                Target = job.Target with { FileName = BuildFileName(job, candidate) },
                State = DownloadJobState.Queued,
                ManifestHash = null,
                PageCount = 0,
                CompletedPages = 0,
                FallbackCandidates = [],
                Warning = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });
        EnsureStarted();
    }

    public void Remove(string jobId) => RemoveManyAsync([jobId]).GetAwaiter().GetResult();

    public async Task RemoveManyAsync(IEnumerable<string> jobIds)
    {
        var ids = jobIds.Distinct(StringComparer.Ordinal).ToArray();
        lock (_gate) _removing.UnionWith(ids);
        try
        {
            await StopMany(ids, null).ConfigureAwait(false);
            Commit(jobs => jobs.RemoveAll(job => ids.Contains(job.JobId, StringComparer.Ordinal)));
            foreach (var id in ids) TryDeleteStaging(id);
        }
        finally { lock (_gate) _removing.ExceptWith(ids); }
    }

    /// <summary>
    /// Whether a job still has staged data. The Download List asks this before
    /// confirming a removal, because deleting staging is only allowed after the
    /// queue state has been committed.
    /// </summary>
    public bool HasStagedData(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return Directory.Exists(Path.Combine(_browser.StagingRoot, "jobs", jobId));
    }

    public void ClearCompleted() => Commit(jobs =>
        jobs.RemoveAll(job => job.State == DownloadJobState.Completed));

    /// <summary>Starts the bounded chapter scheduler. Idempotent.</summary>
    public void Start() => EnsureStarted();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { StopAll(); }
        catch (QueuePersistenceException ex)
        {
            // Tray Exit must still stop owned work if durable state is unavailable.
            // Preserve the file; restart already parks any in-flight records.
            System.Diagnostics.Trace.TraceError("Queue shutdown persistence: {0}", ex.Message);
        }
        finally { Task.WhenAll(_manifestScheduler.ShutdownAsync(), _downloadScheduler.ShutdownAsync()).GetAwaiter().GetResult(); }
    }

    /// <summary>
    /// Loads durable state and applies the restart rule: every in-flight state
    /// becomes Paused, nothing resumes automatically. A job whose file already
    /// exists and validates is reconciled to Completed instead of being
    /// re-downloaded.
    /// </summary>
    private void LoadPersisted()
    {
        var loaded = _store.Load();
        lock (_gate)
        {
            _jobs.Clear();
            foreach (var job in loaded.Jobs)
            {
                _jobs.Add(ReconcileOnLoad(job));
            }
        }

        if (loaded.Warning is not null)
        {
            // A damaged or unknown-version queue is reported, never silently
            // replaced by an empty one.
            QueueSummaryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private DownloadJobRecord ReconcileOnLoad(DownloadJobRecord job)
    {
        // A persisted target is not trusted input: it is JSON a hand edit or a
        // damaged file can change, and Path.Combine would follow a rooted or
        // traversing segment outside the Library. Such a job is failed with the
        // reason in its own warning — the per-job channel the Download List already
        // renders — rather than dropped, so the user still sees which chapter it was
        // and can remove it. A completed job no longer writes, so its record stays
        // history instead of being rewritten into a failure.
        if (job.State != DownloadJobState.Completed
            && !DownloaderPathContainment.TryResolve(
                job.Target.Root,
                job.Target.FolderName,
                job.Target.FileName,
                out _,
                out var containment))
        {
            return job with
            {
                State = DownloadJobState.Failed,
                Warning = UnsafeTargetMessage(containment),
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        }

        // Nothing resumes automatically after a restart, which includes a job
        // that was only queued. States that already require an explicit user
        // action are left as they are.
        if (job.State == DownloadJobState.ManifestReady)
        {
            return HasValidManifest(job)
                ? job
                : job with
                {
                    State = DownloadJobState.Paused,
                    ManifestHash = null,
                    PageCount = 0,
                    Warning = "Manifest cache tidak tersedia; Resume untuk menyelesaikan ulang.",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
        }

        if (job.State is DownloadJobState.Completed
            or DownloadJobState.Failed
            or DownloadJobState.Paused
            or DownloadJobState.AwaitingSourceFallback)
        {
            return job;
        }

        var published = job.PublishedPath;
        if (published is not null && File.Exists(published))
        {
            // Crash after rename but before the queue commit: the file is the
            // evidence, so recognize the job as finished rather than
            // re-downloading a valid chapter.
            return job with
            {
                State = DownloadJobState.Completed,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        }

        return job with
        {
            State = DownloadJobState.Paused,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// One wording for a target the containment rule refused, so the same unsafe
    /// state reads the same whether it was caught on load or at the moment of use.
    /// </summary>
    private static string UnsafeTargetMessage(string? problem) =>
        "Target download tidak aman dan tidak dipakai: " + problem;

    private RemoteChapterIdentity ChapterIdentity(DownloadJobRecord job) =>
        new(
            job.Identity.SourceId,
            new RemoteTitleIdentity(job.Identity.SourceId, job.Identity.TitleId, job.Identity.TitleHid, string.Empty),
            job.Identity.ChapterId,
            job.ChapterNumber,
            new RemoteGroupIdentity(job.Identity.SourceId, job.Identity.GroupId));

    private QueueManifestStore ManifestStore(string jobId) =>
        new(_browser.ResolveContained(Path.Combine("jobs", jobId, "manifest.json")));

    private bool HasValidManifest(DownloadJobRecord job)
    {
        if (string.IsNullOrWhiteSpace(job.ManifestHash)) return false;
        var manifest = ManifestStore(job.JobId).Read(ChapterIdentity(job));
        return manifest is not null && string.Equals(manifest.ManifestHash, job.ManifestHash, StringComparison.Ordinal);
    }

    private void InvalidateManifest(string jobId)
    {
        try
        {
            var path = _browser.ResolveContained(Path.Combine("jobs", jobId, "manifest.json"));
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later resolver ignores an identity/hash mismatch. Failure state
            // remains visible even when the stale cache cannot be deleted now.
        }
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _manifestScheduler.Start();
        _downloadScheduler.Start();
    }

    private async Task RunManifestClaimedAsync(DownloadJobRecord job, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (job.State == DownloadJobState.ResolvingAlternates)
                await ResolveAlternatesAsync(job, token).ConfigureAwait(false);
            else if (job.State == DownloadJobState.RefreshingManifest)
                await RefreshManifestAsync(job, token).ConfigureAwait(false);
            else
                await ResolveManifestAsync(job, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (QueueSessionUnavailableException exception) { StopAll(exception.Message); }
        catch (Exception exception)
        {
            Fail(job.JobId, "Job gagal: " + exception.GetBaseException().Message);
        }
        finally { ParkIfPausing(job.JobId); }
    }

    private async Task RunDownloadClaimedAsync(DownloadJobRecord job, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await DownloadManifestAsync(job, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (QueueSessionUnavailableException exception) { StopAll(exception.Message); }
        catch (Exception exception)
        {
            Fail(job.JobId, "Job gagal: " + exception.GetBaseException().Message);
        }
        finally { ParkIfPausing(job.JobId); }
    }

    private async Task ResolveManifestAsync(DownloadJobRecord job, CancellationToken lifetime)
    {
        // The one point where a job's target is about to be used for a write. A
        // target that cannot be contained inside its own root is refused here, so
        // neither a Retry of a job loaded with unsafe state nor any other route into
        // the scheduler can reach the publisher with it.
        if (!DownloaderPathContainment.TryResolve(
                job.Target.Root,
                job.Target.FolderName,
                job.Target.FileName,
                out _,
                out var containment))
        {
            Fail(job.JobId, UnsafeTargetMessage(containment));
            return;
        }

        lifetime.ThrowIfCancellationRequested();

        var sourceId = job.Identity.SourceId;
        var source = _sources.FindSource(sourceId);
        if (source is null)
        {
            Fail(job.JobId, $"Source '{sourceId}' tidak terdaftar.");
            return;
        }

        var independentMode = _httpTransport?.IsProxyMode == true;
        var manifestSession = independentMode && sourceId == "comix"
            ? new QueueManifestSession(_browser.StagingRoot,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(job.JobId))).ToLowerInvariant()[..32],
                _httpTransport!.Pool, source, _shared,
                status => UpdateRoute(job.JobId, status))
            : null;

        if (manifestSession is null && source is IQueueSourceReadiness readiness)
        {
            var providerState = readiness.GetQueueReadiness();
            if (!providerState.IsReady)
            {
                ParkQueuedSource(
                    sourceId,
                    providerState.BlockedReason
                        ?? $"Source '{source.DisplayName}' belum siap untuk Queue.");
                return;
            }
        }

        var chapterIdentity = ChapterIdentity(job);

        SetState(job.JobId, DownloadJobState.Resolving, "Menyelesaikan manifest");

        RemoteChapterManifest manifest;
        try
        {
            var snapshot = ManifestStore(job.JobId);
            manifest = snapshot.Read(chapterIdentity)
                ?? await (manifestSession is null
                    ? source.GetManifestAsync(chapterIdentity, lifetime)
                    : manifestSession.GetAsync(chapterIdentity, lifetime)).ConfigureAwait(false);
            snapshot.Write(manifest);
            lifetime.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            ParkIfPausing(job.JobId);
            return;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or QueueSessionUnavailableException))
        {
            Fail(job.JobId, "Manifest gagal diselesaikan: " + exception.GetBaseException().Message);
            return;
        }

        // Persist the intended identity and target before any effect that
        // could leave an ambiguously owned file behind.
        Commit(jobs =>
        {
            var index = FindIndex(jobs, job.JobId);
            if (index < 0 || lifetime.IsCancellationRequested || jobs[index].State == DownloadJobState.Pausing) return;
            jobs[index] = jobs[index] with
            {
                ManifestHash = manifest.ManifestHash,
                PageCount = manifest.PageCount,
                State = DownloadJobState.ManifestReady,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });

        // Scheduler handoff happens only after this resolver settles and its
        // proxy lease is released. The download scheduler is woken by the
        // manifest scheduler's Settled event, so one job cannot occupy both
        // lanes simultaneously.
    }

    private async Task DownloadManifestAsync(DownloadJobRecord job, CancellationToken lifetime)
    {
        if (!DownloaderPathContainment.TryResolve(
                job.Target.Root,
                job.Target.FolderName,
                job.Target.FileName,
                out _,
                out var containment))
        {
            Fail(job.JobId, UnsafeTargetMessage(containment));
            return;
        }

        var sourceId = job.Identity.SourceId;
        var source = _sources.FindSource(sourceId);
        if (source is null)
        {
            Fail(job.JobId, $"Source '{sourceId}' tidak terdaftar.");
            return;
        }

        var chapterIdentity = ChapterIdentity(job);
        var snapshot = ManifestStore(job.JobId);
        var manifest = snapshot.Read(chapterIdentity);
        if (manifest is null)
        {
            Transition(job.JobId, current => current.State == DownloadJobState.ManifestReady
                ? current with
                {
                    State = DownloadJobState.Queued,
                    ManifestHash = null,
                    PageCount = 0,
                    Warning = "Manifest cache tidak tersedia; akan diselesaikan ulang.",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                }
                : current);
            return;
        }

        // A scheduler claim is not yet a visible state transition. Mark it
        // before the first page worker runs so page progress is accepted and
        // the table does not leave an actively downloading job at
        // "Manifest ready" until it suddenly publishes.
        SetState(job.JobId, DownloadJobState.Downloading, null);

        var independentMode = _httpTransport?.IsProxyMode == true;
        using var independent = independentMode
            ? new QueueIndependentSession(job.JobId, sourceId, _httpTransport!.Pool, _shared,
                status => UpdateRoute(job.JobId, status)) : null;

        lifetime.ThrowIfCancellationRequested();
        using var transport = new PageTransport(_browser, _browser.StagingRoot, _httpTransport, independent);
        // Page work never resolves a manifest. A retry re-enters the manifest
        // lane, keeping browser/proxy bootstrap separate from page transfer.
        var pipeline = new ChapterDownloadPipeline(transport, source, _browser.StagingRoot);
        var progress = new InlineProgress(update => UpdateProgress(job.JobId, update));

        PipelineResult result;
        try
        {
            result = await pipeline.RunAsync(
                job,
                manifest,
                ComixReferer(sourceId),
                progress,
                lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ParkIfPausing(job.JobId);
            return;
        }
        catch (QueueSessionUnavailableException) { throw; }
        catch (Exception exception)
        {
            Fail(job.JobId, "Download pipeline gagal: " + exception.GetBaseException().Message);
            return;
        }

        if (result.ManifestConflict)
        {
            InvalidateManifest(job.JobId);
            TryDeleteStaging(job.JobId);
            Commit(jobs =>
            {
                var index = FindIndex(jobs, job.JobId);
                if (index < 0) return;
                jobs[index] = jobs[index] with
                {
                    State = DownloadJobState.Failed,
                    ManifestHash = null,
                    PageCount = 0,
                    Warning = result.Detail ?? "Manifest berubah; Retry akan menyelesaikan ulang.",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            });
            return;
        }

        if (result.ManifestRefreshRequested)
        {
            SetState(job.JobId, DownloadJobState.RefreshingManifest,
                "Menyegarkan manifest untuk page yang gagal");
            return;
        }

        var publishIncomplete = result.CanPublishIncomplete(manifest.PageCount);
        if (independentMode && result.FailureEvidence.Any(failure => failure.Outcome is
                PageFetchOutcome.Rejected or PageFetchOutcome.Challenge or PageFetchOutcome.Throttled))
        {
            Fail(job.JobId, "Provider rejected/challenged or throttled the request; no proxy rotation or further provider requests. "
                + string.Join("; ", result.FailureEvidence.Select(item => item.Detail).Distinct()));
            return;
        }
        if (!result.Complete && !publishIncomplete)
        {
            await EnterFallbackOrFailAsync(job, result)
                .ConfigureAwait(false);
            return;
        }

        SetState(job.JobId, DownloadJobState.Publishing, "Mempublikasikan CBZ");

        DownloadJobRecord current;
        lock (_gate)
        {
            current = _jobs[FindIndex(_jobs, job.JobId)];
        }

        var publisher = new CbzChapterPublisher();
        PublicationOutcome outcome;
        try
        {
            outcome = publishIncomplete
                ? await publisher.PublishIncompleteAsync(
                    chapterIdentity,
                    current.GroupDisplayName,
                    manifest.ManifestHash,
                    result.Pages,
                    manifest.PageCount,
                    result.FailureEvidence,
                    current.Target,
                    lifetime).ConfigureAwait(false)
                : await publisher.PublishAsync(
                    chapterIdentity,
                    current.GroupDisplayName,
                    manifest.ManifestHash,
                    result.Pages,
                    current.Target,
                    lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ParkIfPausing(job.JobId);
            return;
        }

        if (!outcome.Published || outcome.Path is null)
        {
            Fail(job.JobId, outcome.ConflictReason ?? "Publikasi gagal.");
            return;
        }

        // Completion is only claimed once both the file and the durable state
        // agree.
        var publishedPath = outcome.Path;
        Commit(jobs =>
        {
            var index = FindIndex(jobs, job.JobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                State = DownloadJobState.Completed,
                PublishedPath = publishedPath,
                CompletedPages = result.Pages.Count,
                Warning = outcome.Warnings.Count == 0 ? null : string.Join(" ", outcome.Warnings),
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });

        if (!_index.TryRecordPublished(current.Identity, Path.GetFileName(publishedPath), out var indexWarning))
        {
            // A nonessential index failure leaves the published CBZ intact and
            // surfaces as a repairable warning.
            Warn(job.JobId, indexWarning);
        }

        TryDeleteStaging(job.JobId);
    }

    /// <summary>
    /// Refreshes only the source manifest after page retries have settled. It
    /// runs in the manifest lane, so it preserves the proxy/session boundary
    /// and never lets a page worker issue a provider request.
    /// </summary>
    private async Task RefreshManifestAsync(DownloadJobRecord job, CancellationToken lifetime)
    {
        var source = _sources.FindSource(job.Identity.SourceId);
        if (source is null)
        {
            Fail(job.JobId, $"Source '{job.Identity.SourceId}' tidak terdaftar.");
            return;
        }

        if (string.IsNullOrWhiteSpace(job.ManifestHash))
        {
            Fail(job.JobId, "Manifest cache tidak memiliki hash; Retry akan menyelesaikan ulang.");
            return;
        }

        var independentMode = _httpTransport?.IsProxyMode == true;
        var manifestSession = independentMode && job.Identity.SourceId == "comix"
            ? new QueueManifestSession(_browser.StagingRoot,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(job.JobId + "/refresh"))).ToLowerInvariant()[..32],
                _httpTransport!.Pool, source, _shared,
                status => UpdateRoute(job.JobId, status))
            : null;
        if (manifestSession is null && source is IQueueSourceReadiness readiness)
        {
            var providerState = readiness.GetQueueReadiness();
            if (!providerState.IsReady)
            {
                ParkQueuedSource(job.Identity.SourceId, providerState.BlockedReason
                    ?? $"Source '{source.DisplayName}' belum siap untuk Queue.");
                return;
            }
        }

        RemoteChapterManifest refreshed;
        try
        {
            var chapter = ChapterIdentity(job);
            refreshed = await (manifestSession is null
                ? source.GetManifestAsync(chapter, lifetime)
                : manifestSession.GetAsync(chapter, lifetime)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (QueueSessionUnavailableException) { throw; }
        catch (Exception exception)
        {
            // Preserve the existing whole-chapter fallback behavior, but only
            // after the refresh owner has released its browser/proxy lease.
            Commit(jobs =>
            {
                var index = FindIndex(jobs, job.JobId);
                if (index < 0) return;
                jobs[index] = jobs[index] with
                {
                    State = DownloadJobState.ResolvingAlternates,
                    Warning = "Refresh manifest gagal: " + exception.GetBaseException().Message,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            });
            return;
        }

        if (!string.Equals(refreshed.ManifestHash, job.ManifestHash, StringComparison.Ordinal))
        {
            InvalidateManifest(job.JobId);
            TryDeleteStaging(job.JobId);
            Commit(jobs =>
            {
                var index = FindIndex(jobs, job.JobId);
                if (index < 0) return;
                jobs[index] = jobs[index] with
                {
                    State = DownloadJobState.Failed,
                    ManifestHash = null,
                    PageCount = 0,
                    Warning = "Manifest berubah saat recovery; Retry akan menyelesaikan ulang.",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            });
            return;
        }

        ManifestStore(job.JobId).Write(refreshed);
        Commit(jobs =>
        {
            var index = FindIndex(jobs, job.JobId);
            if (index < 0 || lifetime.IsCancellationRequested || jobs[index].State == DownloadJobState.Pausing) return;
            jobs[index] = jobs[index] with
            {
                State = DownloadJobState.ManifestReady,
                Warning = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });
    }

    /// <summary>
    /// Alternate-group lookup is manifest-side work. The page worker records
    /// the request and releases page resources before this runs.
    /// </summary>
    private Task EnterFallbackOrFailAsync(DownloadJobRecord job, PipelineResult result)
    {
        var detail = result.Detail ?? $"{result.FailedPages.Count} page gagal setelah recovery.";
        Commit(jobs =>
        {
            var index = FindIndex(jobs, job.JobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                State = DownloadJobState.ResolvingAlternates,
                Warning = detail,
                FallbackCandidates = [],
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });
        return Task.CompletedTask;
    }

    private async Task ResolveAlternatesAsync(DownloadJobRecord job, CancellationToken lifetime)
    {
        var source = _sources.FindSource(job.Identity.SourceId);
        if (source is null)
        {
            Fail(job.JobId, $"Source '{job.Identity.SourceId}' tidak terdaftar.");
            return;
        }

        var independentMode = _httpTransport?.IsProxyMode == true;
        var manifestSession = independentMode && job.Identity.SourceId == "comix"
            ? new QueueManifestSession(_browser.StagingRoot,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(job.JobId + "/alternate"))).ToLowerInvariant()[..32],
                _httpTransport!.Pool, source, _shared,
                status => UpdateRoute(job.JobId, status))
            : null;
        if (manifestSession is null && source is IQueueSourceReadiness readiness)
        {
            var providerState = readiness.GetQueueReadiness();
            if (!providerState.IsReady)
            {
                ParkQueuedSource(job.Identity.SourceId, providerState.BlockedReason
                    ?? $"Source '{source.DisplayName}' belum siap untuk Queue.");
                return;
            }
        }

        IReadOnlyList<RemoteAlternateChapter> candidates = [];
        try
        {
            candidates = await (manifestSession is null
                ? source.FindAlternateGroupsAsync(ChapterIdentity(job), lifetime)
                : manifestSession.FindAlternatesAsync(ChapterIdentity(job), lifetime)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (QueueSessionUnavailableException) { throw; }
        catch (Exception exception)
        {
            Fail(job.JobId, (job.Warning ?? "Page download gagal.")
                + " Lookup group alternatif gagal: " + exception.GetBaseException().Message);
            return;
        }

        if (candidates.Count == 0)
        {
            Fail(job.JobId, (job.Warning ?? "Page download gagal.") + " Tidak ada group alternatif yang ditemukan.");
            return;
        }

        Commit(jobs =>
        {
            var index = FindIndex(jobs, job.JobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                State = DownloadJobState.AwaitingSourceFallback,
                Warning = job.Warning,
                FallbackCandidates =
                [
                    .. candidates.Select(candidate => new SourceFallbackCandidate(
                        candidate.Group.GroupId,
                        candidate.GroupDisplayName,
                        candidate.ChapterId,
                        candidate.Reason)),
                ],
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });
    }

    private static string ComixReferer(string sourceId) =>
        string.Equals(sourceId, "comix", StringComparison.Ordinal)
            ? "https://comix.ws/"
            : string.Empty;

    private void UpdateProgress(string jobId, ChapterDownloadProgress update)
    {
        var changed = false;
        lock (_gate)
        {
            var index = FindIndex(_jobs, jobId);
            if (index >= 0)
            {
                var current = _jobs[index];
                if (!current.IsInFlight || current.State == DownloadJobState.Pausing) return;
                var pageCount = update.PageCount == 0 ? current.PageCount : update.PageCount;
                if (current.CompletedPages != update.CompletedPages
                    || current.PageCount != pageCount
                    || !string.Equals(current.Warning, update.StatusText, StringComparison.Ordinal))
                {
                    // Per-page durability belongs to the staging journal. Keeping
                    // this projection in memory avoids rewriting the complete
                    // queue.json for every downloaded page; the next durable state
                    // transition persists the latest projection with the job.
                    _jobs[index] = current with
                    {
                        CompletedPages = Math.Max(current.CompletedPages, update.CompletedPages),
                        PageCount = pageCount,
                        Warning = update.StatusText,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    };
                    changed = true;
                }
            }
        }

        if (changed) Notify();
    }

    private void UpdateRoute(string jobId, string status)
    {
        lock (_gate)
        {
            var index = FindIndex(_jobs, jobId);
            if (index < 0 || !_jobs[index].IsInFlight || _jobs[index].State == DownloadJobState.Pausing) return;
            _jobs[index] = _jobs[index] with { RouteText = status };
        }
        Notify();
    }

    private sealed class InlineProgress(Action<ChapterDownloadProgress> report) : IProgress<ChapterDownloadProgress>
    {
        public void Report(ChapterDownloadProgress value) => report(value);
    }

    private void SetState(string jobId, DownloadJobState state, string? warning) =>
        Commit(jobs =>
        {
            var index = FindIndex(jobs, jobId);
            if (index < 0 || jobs[index].State is DownloadJobState.Pausing or DownloadJobState.Completed) return;
            jobs[index] = jobs[index] with
            {
                State = state,
                Warning = warning,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });

    /// <summary>
    /// Parks every queued job for one unavailable provider in one durable
    /// mutation. This prevents a Resume batch from probing the same missing
    /// prerequisite once per row while unrelated providers remain runnable.
    /// </summary>
    private void ParkQueuedSource(string sourceId, string reason) =>
        Commit(jobs =>
        {
            for (var index = 0; index < jobs.Count; index++)
            {
                var current = jobs[index];
                if (current.State is not (DownloadJobState.Queued
                    or DownloadJobState.RefreshingManifest
                    or DownloadJobState.ResolvingAlternates)
                    || !string.Equals(current.Identity.SourceId, sourceId, StringComparison.Ordinal))
                {
                    continue;
                }

                jobs[index] = current with
                {
                    State = DownloadJobState.Paused,
                    Warning = reason,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            }
        });

    private void Fail(string jobId, string reason, DownloadJobState state = DownloadJobState.Failed) =>
        Commit(jobs =>
        {
            var index = FindIndex(jobs, jobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                State = state,
                Warning = reason,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });

    private void Warn(string jobId, string? warning)
    {
        if (warning is null) return;
        Commit(jobs =>
        {
            var index = FindIndex(jobs, jobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                Warning = warning,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });
    }

    /// <summary>
    /// A pause that interrupted bounded work parks the job once that work has
    /// stopped. The browser is never terminated here.
    /// </summary>
    private void ParkIfPausing(string jobId)
    {
        lock (_gate)
        {
            var index = FindIndex(_jobs, jobId);
            if (index < 0 || _jobs[index].State != DownloadJobState.Pausing) return;
        }
        Commit(jobs =>
        {
            var index = FindIndex(jobs, jobId);
            if (index < 0) return;
            if (jobs[index].State != DownloadJobState.Pausing) return;
            jobs[index] = jobs[index] with
            {
                State = DownloadJobState.Paused,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });
    }

    private void TryDeleteStaging(string jobId)
    {
        try
        {
            var jobsRoot = Path.Combine(_browser.StagingRoot, "jobs", jobId);
            if (Directory.Exists(jobsRoot)) Directory.Delete(jobsRoot, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Leftover staging is recoverable; it never blocks completion.
        }
    }

    /// <summary>
    /// Applies one job transition. Cancelling the bounded work a pause interrupts is
    /// an effect, so it is decided inside the mutation but carried out only after
    /// <see cref="Commit"/> has returned — that is, once the transition is durable.
    /// Cancelling first would leave a job whose save failed visibly unchanged while
    /// its download had already been stopped: state saying <c>Downloading</c> with no
    /// worker behind it.
    /// </summary>
    private void Transition(string jobId, Func<DownloadJobRecord, DownloadJobRecord> map)
    {
        var cancelRunning = false;
        Commit(jobs =>
        {
            var index = FindIndex(jobs, jobId);
            if (index < 0) return;
            var mapped = map(jobs[index]);
            if (!ReferenceEquals(mapped, jobs[index])) jobs[index] = mapped;
            cancelRunning = mapped.State == DownloadJobState.Pausing;
        });

        if (!cancelRunning) return;

        lock (_gate)
        {
            _ = CancelActive([jobId]);
        }
    }

    /// <summary>
    /// Applies one state mutation, persists the whole queue atomically, and only
    /// then adopts the result and notifies. The mutation runs on a clone, so a
    /// <see cref="QueuePersistenceException"/> leaves the in-memory queue exactly
    /// as it was and raises no change signal: a transition that did not become
    /// durable must not become visible either, and on-disk staging is never left
    /// ambiguously owned. The exception still reaches the caller, which reports it.
    ///
    /// Saving happens inside the gate. Two commits running concurrently would
    /// otherwise clone the same base and the second save would silently drop the
    /// first transition. Notification stays outside it, so a handler that reads a
    /// snapshot can never wait on the file write.
    /// </summary>
    private void Commit(Action<List<DownloadJobRecord>> mutate)
    {
        List<DownloadJobRecord> next;
        lock (_gate)
        {
            next = [.. _jobs];
            mutate(next);
            _store.Save(next);
            _jobs = next;
        }

        Notify();
    }

    private void Notify() => QueueSummaryChanged?.Invoke(this, EventArgs.Empty);

    private static int FindIndex(List<DownloadJobRecord> jobs, string jobId) =>
        jobs.FindIndex(candidate => string.Equals(candidate.JobId, jobId, StringComparison.Ordinal));

    /// <summary>
    /// Deterministic, sanitized, naturally sortable, and it carries the
    /// selected group so two variants of one chapter number never collide.
    /// </summary>
    internal static string BuildFileName(RemoteChapterSummary chapter, RemoteSourceGroup group)
    {
        var number = NormalizeNumber(chapter.Identity.ChapterNumber);
        var label = SanitizeFolder(chapter.DisplayName);
        var groupLabel = SanitizeFolder(group.DisplayName);
        return $"{number} - {label} [{groupLabel}].cbz";
    }

    private static string BuildFileName(DownloadJobRecord job, SourceFallbackCandidate candidate)
    {
        var number = NormalizeNumber(job.ChapterNumber);
        var label = SanitizeFolder(job.ChapterDisplayName);
        var groupLabel = SanitizeFolder(candidate.GroupName);
        return $"{number} - {label} [{groupLabel}].cbz";
    }

    /// <summary>
    /// Zero-pads a whole chapter number so natural order matches sort order,
    /// and keeps a decimal chapter number readable and sortable. The "D"
    /// specifier is integer-only, so decimals use a custom numeric format.
    /// </summary>
    private static string NormalizeNumber(string chapterNumber)
    {
        if (long.TryParse(
                chapterNumber,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var integer))
        {
            return integer.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (double.TryParse(
                chapterNumber,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return value.ToString("0000.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(chapterNumber) ? "0000" : SanitizeFolder(chapterNumber);
    }

    /// <summary>
    /// Removes everything that is unsafe in a path segment. A collision is
    /// surfaced by the publisher's provenance check, never hidden by a "(2)".
    /// </summary>
    internal static string SanitizeFolder(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ' ')
            {
                builder.Append(character);
            }
            else if (Path.GetInvalidFileNameChars().Contains(character))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(character);
            }
        }

        var trimmed = builder.ToString().Trim().TrimEnd('.');
        if (trimmed.Length == 0 || trimmed is "." or "..") return "untitled";
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }
}
