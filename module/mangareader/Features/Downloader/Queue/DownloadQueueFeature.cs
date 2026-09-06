using System.IO;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;

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
    private readonly LibraryRootContext _root;
    private readonly MangaSourceRegistry _sources;
    private readonly DownloaderPyHostClient _browser;
    private readonly DownloadQueueStore _store;
    private readonly DownloadSourceIndex _index;
    private readonly object _gate = new();
    private List<DownloadJobRecord> _jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _scheduler;
    private bool _started;
    private int _disposed;

    public DownloadQueueFeature(
        LibraryRootContext root,
        MangaSourceRegistry sources,
        DownloaderPyHostClient browser,
        DownloadQueueStore store,
        DownloadSourceIndex index)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _index = index ?? throw new ArgumentNullException(nameof(index));
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
            return new QueueSummary(
                _jobs.Count,
                _jobs.Count(job => job.State is not (DownloadJobState.Paused
                    or DownloadJobState.Failed
                    or DownloadJobState.Completed)),
                _jobs.Count(job => job.State is DownloadJobState.Paused or DownloadJobState.Pausing),
                _jobs.Count(job => job.State == DownloadJobState.Failed));
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
        HashSet<string> live;
        lock (_gate)
        {
            live = LiveIdentityKeys(_jobs);
        }

        var skippedPublished = 0;
        var skippedQueued = 0;
        var candidates = new List<DownloadJobRecord>();
        foreach (var chapter in chapters)
        {
            var identity = new DownloadJobIdentity(
                chapter.Identity.SourceId,
                title.Identity.TitleId,
                title.Identity.TitleHid,
                chapter.Identity.ChapterId,
                group.Identity.GroupId);

            if (live.Contains(identity.Key))
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
            var taken = LiveIdentityKeys(jobs);
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

        if (appended.Count > 0) EnsureStarted();
        return new QueueAddResult(appended.Count, skippedPublished, skippedQueued, null);
    }

    /// <summary>
    /// The one definition of an identity that is already spoken for: a job that has
    /// not finished. A replacement permission covers chapters that are already
    /// published; it never covers work still in flight, which would download one
    /// chapter twice into one file.
    /// </summary>
    private static HashSet<string> LiveIdentityKeys(IEnumerable<DownloadJobRecord> jobs) =>
        jobs.Where(job => !job.IsTerminal)
            .Select(job => job.Identity.Key)
            .ToHashSet(StringComparer.Ordinal);

    public void Pause(string jobId) => Transition(jobId, job => job.State switch
    {
        DownloadJobState.Completed or DownloadJobState.Failed => job,
        DownloadJobState.Queued => job with
        {
            State = DownloadJobState.Paused,
            UpdatedUtc = DateTimeOffset.UtcNow,
        },
        _ => job with
        {
            State = DownloadJobState.Pausing,
            UpdatedUtc = DateTimeOffset.UtcNow,
        },
    });

    public void Resume(string jobId)
    {
        Transition(jobId, job => job.State is DownloadJobState.Paused or DownloadJobState.Failed
            ? job with
            {
                State = DownloadJobState.Queued,
                Warning = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
            }
            : job);
        EnsureStarted();
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

    public void Remove(string jobId)
    {
        string? stagingJobId = null;
        Commit(jobs =>
        {
            var index = jobs.FindIndex(candidate =>
                string.Equals(candidate.JobId, jobId, StringComparison.Ordinal));
            if (index < 0) return;

            stagingJobId = jobs[index].JobId;
            jobs.RemoveAt(index);
        });

        // Staging is deleted only after the queue state is committed, so a
        // crash in between can never leave an orphaned job pointing at deleted
        // pages.
        if (stagingJobId is not null)
        {
            TryDeleteStaging(stagingJobId);
        }
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

    /// <summary>Starts the single-job scheduler. Idempotent.</summary>
    public void Start() => EnsureStarted();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _lifetime.Cancel();
        lock (_gate)
        {
            foreach (var source in _running.Values) source.Cancel();
            _running.Clear();
        }

        try
        {
            _scheduler?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Shutdown does not need the scheduler's failure.
        }

        _lifetime.Dispose();
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

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _scheduler = Task.Run(() => RunSchedulerAsync(_lifetime.Token));
        }
    }

    /// <summary>
    /// One active chapter at a time, in durable queue order. A job that is
    /// paused while running is left to finish its bounded work and park.
    /// </summary>
    private async Task RunSchedulerAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            DownloadJobRecord? next = null;
            lock (_gate)
            {
                next = _jobs.FirstOrDefault(job => job.State == DownloadJobState.Queued);
                if (next is not null)
                {
                    var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    _running[next.JobId] = source;
                }
            }

            if (next is null)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await RunJobAsync(next, lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Fail(next.JobId, "Job gagal: " + exception.GetBaseException().Message);
            }
            finally
            {
                lock (_gate)
                {
                    if (_running.Remove(next.JobId, out var source)) source.Dispose();
                }
            }
        }
    }

    private async Task RunJobAsync(DownloadJobRecord job, CancellationToken lifetime)
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

        CancellationTokenSource linked;
        lock (_gate)
        {
            if (!_running.TryGetValue(job.JobId, out linked!))
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            }
        }

        var sourceId = job.Identity.SourceId;
        var registrationEntry = _sources.Find(sourceId);
        if (registrationEntry is null)
        {
            Fail(job.JobId, $"Source '{sourceId}' tidak terdaftar.");
            return;
        }

        var source = registrationEntry.Source;
        var identity = new RemoteTitleIdentity(
            job.Identity.SourceId,
            job.Identity.TitleId,
            job.Identity.TitleHid,
            string.Empty);
        var chapterIdentity = new RemoteChapterIdentity(
            job.Identity.SourceId,
            identity,
            job.Identity.ChapterId,
            job.ChapterNumber,
            new RemoteGroupIdentity(job.Identity.SourceId, job.Identity.GroupId));

        SetState(job.JobId, DownloadJobState.Resolving, "Menyelesaikan manifest");

        RemoteChapterManifest manifest;
        try
        {
            manifest = await source.GetManifestAsync(chapterIdentity, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ParkIfPausing(job.JobId);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(job.JobId, "Manifest gagal diselesaikan: " + exception.GetBaseException().Message);
            return;
        }

        // Persist the intended identity and target before any effect that
        // could leave an ambiguously owned file behind.
        Commit(jobs =>
        {
            var index = FindIndex(jobs, job.JobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                ManifestHash = manifest.ManifestHash,
                PageCount = manifest.PageCount,
                State = DownloadJobState.Downloading,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        });
        Notify();

        var transport = new PageTransport(_browser, _browser.StagingRoot);
        var pipeline = new ChapterDownloadPipeline(transport, source, _browser.StagingRoot);
        var progress = new Progress<ChapterDownloadProgress>(update =>
            UpdateProgress(job.JobId, update));

        PipelineResult result;
        try
        {
            result = await pipeline.RunAsync(
                job,
                manifest,
                ComixReferer(sourceId),
                progress,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ParkIfPausing(job.JobId);
            return;
        }

        if (result.ManifestConflict)
        {
            Fail(job.JobId, result.Detail ?? "Manifest berubah.");
            return;
        }

        if (!result.Complete)
        {
            await EnterFallbackOrFailAsync(job, source, chapterIdentity, result, linked.Token)
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
            outcome = await publisher.PublishAsync(
                chapterIdentity,
                current.GroupDisplayName,
                manifest.ManifestHash,
                result.Pages,
                current.Target,
                linked.Token).ConfigureAwait(false);
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
                CompletedPages = manifest.PageCount,
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
    /// A chapter whose pages could not all be recovered asks the source for
    /// other groups publishing the same chapter number. With candidates the job
    /// waits for explicit user confirmation; without any, it fails with its
    /// staging retained for Retry or Remove.
    /// </summary>
    private async Task EnterFallbackOrFailAsync(
        DownloadJobRecord job,
        IMangaSource source,
        RemoteChapterIdentity chapterIdentity,
        PipelineResult result,
        CancellationToken cancellationToken)
    {
        var detail = result.Detail ?? $"{result.FailedPages.Count} page gagal setelah recovery.";
        IReadOnlyList<RemoteAlternateChapter> candidates = [];
        try
        {
            candidates = await source
                .FindAlternateGroupsAsync(chapterIdentity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreachable provider leaves the job failed with staging intact;
            // it must not be reported as "no alternate group exists".
        }

        if (candidates.Count == 0)
        {
            Fail(job.JobId, detail + " Tidak ada group alternatif yang ditemukan.");
            return;
        }

        Commit(jobs =>
        {
            var index = FindIndex(jobs, job.JobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                State = DownloadJobState.AwaitingSourceFallback,
                Warning = detail,
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
        try
        {
            Commit(jobs =>
            {
                var index = FindIndex(jobs, jobId);
                if (index < 0) return;
                jobs[index] = jobs[index] with
                {
                    CompletedPages = update.CompletedPages,
                    PageCount = update.PageCount == 0 ? jobs[index].PageCount : update.PageCount,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            });
        }
        catch (QueuePersistenceException)
        {
            // Progress is not a transition that can leave staging ambiguously
            // owned, so a failed save here is dropped rather than thrown at the
            // UI thread. The durable transitions still enforce it.
        }
    }

    private void SetState(string jobId, DownloadJobState state, string? warning) =>
        Commit(jobs =>
        {
            var index = FindIndex(jobs, jobId);
            if (index < 0) return;
            jobs[index] = jobs[index] with
            {
                State = state,
                Warning = warning,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
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
            if (_running.TryGetValue(jobId, out var source)) source.Cancel();
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
            // Cancel bounded native work; an already-written pyhost command still
            // reaches its own terminal response. A job that finished in the meantime
            // has already left the running set, and there is nothing to cancel.
            if (_running.TryGetValue(jobId, out var source)) source.Cancel();
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
