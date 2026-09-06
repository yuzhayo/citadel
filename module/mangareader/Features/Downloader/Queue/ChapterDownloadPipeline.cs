using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Queue;

public sealed record ChapterDownloadProgress(
    int CompletedPages,
    int PageCount,
    string StatusText);

/// <summary>
/// One chapter's staging result. <see cref="FailedPages"/> is the authority on
/// completeness: a chapter is never publishable while it is non-empty.
/// </summary>
public sealed record PipelineResult(
    bool Complete,
    IReadOnlyList<StagedPage> Pages,
    IReadOnlyList<int> FailedPages,
    string? Detail,
    bool ManifestConflict)
{
    public static PipelineResult Conflict(string detail) =>
        new(false, [], [], detail, ManifestConflict: true);
}

/// <summary>The per-page staging journal written to a job's own job.json.</summary>
internal sealed record JobJournal(
    string ManifestHash,
    List<StagedPageRecord> Pages);

/// <summary>
/// Owns one chapter's download: bounded page concurrency, the fixed retry
/// schedule, failed-page-only recovery, source-owned transforms, and the
/// staging journal that makes resume safe. It holds no job state — the queue
/// feature owns that — and it never publishes.
/// </summary>
public sealed class ChapterDownloadPipeline
{
    /// <summary>Retries after the initial attempt, per pass.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    /// <summary>Initial default: two concurrently streamed pages.</summary>
    public const int PageConcurrency = 2;

    private readonly PageTransport _transport;
    private readonly IMangaSource _source;
    private readonly string _stagingRoot;
    private readonly string _jobsRoot;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public ChapterDownloadPipeline(
        PageTransport transport,
        IMangaSource source,
        string stagingRoot,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _jobsRoot = Path.Combine(_stagingRoot, "jobs");
        _retryDelays = retryDelays ?? RetryDelays;
    }

    public string StagingRoot => _stagingRoot;

    /// <summary>One job's staging folder, always inside the staging root.</summary>
    public string JobRoot(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var candidate = Path.GetFullPath(Path.Combine(_jobsRoot, jobId));
        if (!candidate.StartsWith(_jobsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("job id keluar dari root staging: " + jobId);
        }

        return candidate;
    }

    public async Task<PipelineResult> RunAsync(
        DownloadJobRecord job,
        RemoteChapterManifest manifest,
        string referer,
        IProgress<ChapterDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(manifest);

        var jobRoot = JobRoot(job.JobId);
        Directory.CreateDirectory(Path.Combine(jobRoot, "pages"));

        // A refreshed manifest that changed identity or order is a visible
        // conflict; old and new staging are never mixed.
        var journal = ReadJournal(jobRoot);
        if (journal is not null
            && !string.Equals(journal.ManifestHash, manifest.ManifestHash, StringComparison.Ordinal))
        {
            return PipelineResult.Conflict(
                "Manifest provider berubah sejak staging dibuat; job perlu dimulai ulang.");
        }

        var records = new Dictionary<int, StagedPageRecord>(
            (journal?.Pages ?? []).ToDictionary(record => record.Ordinal));
        var failed = new SortedSet<int>();
        var completed = 0;

        Report(progress, completed, manifest.PageCount, "Memulai chapter");

        // First pass: every expected page, continuing past failures.
        completed = await RunPassAsync(
            manifest, referer, jobRoot, records, failed,
            Enumerable.Range(0, manifest.PageCount).ToList(),
            progress, completed, cancellationToken).ConfigureAwait(false);

        if (failed.Count > 0)
        {
            // Recovery pass: refresh the same chapter and retry only the
            // failures, never the pages that already validated.
            RemoteChapterManifest refreshed;
            try
            {
                refreshed = await _source.GetManifestAsync(manifest.Chapter, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new PipelineResult(
                    false,
                    [],
                    [.. failed],
                    "Recovery gagal menyegarkan manifest: " + exception.GetBaseException().Message,
                    ManifestConflict: false);
            }

            if (!string.Equals(refreshed.ManifestHash, manifest.ManifestHash, StringComparison.Ordinal))
            {
                return PipelineResult.Conflict(
                    "Manifest berubah saat recovery; job perlu dimulai ulang.");
            }

            Report(progress, completed, manifest.PageCount, $"Memulihkan {failed.Count} page gagal");
            var retryOrdinals = failed.ToList();
            failed.Clear();
            completed = await RunPassAsync(
                refreshed, referer, jobRoot, records, failed,
                retryOrdinals, progress, completed, cancellationToken).ConfigureAwait(false);
        }

        WriteJournal(jobRoot, manifest.ManifestHash, records);

        if (failed.Count > 0)
        {
            return new PipelineResult(
                false,
                [],
                [.. failed],
                $"{failed.Count} page tetap gagal setelah recovery.",
                ManifestConflict: false);
        }

        var staged = new List<StagedPage>(manifest.PageCount);
        for (var ordinal = 0; ordinal < manifest.PageCount; ordinal++)
        {
            if (!records.TryGetValue(ordinal, out var record))
            {
                return new PipelineResult(
                    false,
                    [],
                    [ordinal],
                    $"Page {ordinal} tidak punya catatan staging.",
                    ManifestConflict: false);
            }

            staged.Add(new StagedPage(ordinal, Path.Combine(jobRoot, record.RelativePath), record.Format));
        }

        Report(progress, manifest.PageCount, manifest.PageCount, "Lengkap");
        return new PipelineResult(true, staged, [], null, ManifestConflict: false);
    }

    /// <summary>Removes one job's staging. Only called after queue state is committed.</summary>
    public void DeleteStaging(string jobId)
    {
        var jobRoot = JobRoot(jobId);
        if (Directory.Exists(jobRoot))
        {
            Directory.Delete(jobRoot, recursive: true);
        }
    }

    private async Task<int> RunPassAsync(
        RemoteChapterManifest manifest,
        string referer,
        string jobRoot,
        Dictionary<int, StagedPageRecord> records,
        SortedSet<int> failed,
        IReadOnlyList<int> ordinals,
        IProgress<ChapterDownloadProgress>? progress,
        int completed,
        CancellationToken cancellationToken)
    {
        var counter = completed;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = PageConcurrency,
        };

        await Parallel.ForEachAsync(ordinals, options, async (ordinal, token) =>
        {
            var page = manifest.Pages[ordinal];

            // Resume reuses a page only when its record and its bytes still
            // validate against the current immutable manifest.
            if (records.TryGetValue(ordinal, out var existing)
                && string.Equals(existing.RemoteKey, page.RemoteKey, StringComparison.Ordinal)
                && IsReusable(Path.Combine(jobRoot, existing.RelativePath), existing))
            {
                var reused = Interlocked.Increment(ref counter);
                Report(progress, reused, manifest.PageCount, $"Page {ordinal + 1} dipakai ulang");
                return;
            }

            var record = await AttemptPageAsync(page, ordinal, jobRoot, referer, manifest, token)
                .ConfigureAwait(false);
            if (record is null)
            {
                lock (failed)
                {
                    failed.Add(ordinal);
                }

                Report(progress, Volatile.Read(ref counter), manifest.PageCount, $"Page {ordinal + 1} gagal");
                return;
            }

            lock (records)
            {
                records[ordinal] = record;
            }

            var done = Interlocked.Increment(ref counter);
            Report(progress, done, manifest.PageCount, $"Page {ordinal + 1} tersimpan");
        }).ConfigureAwait(false);

        return Volatile.Read(ref counter);
    }

    /// <summary>
    /// One page: an initial attempt plus up to three retries on the fixed
    /// 1/2/4 second schedule. Whether a failed attempt may use the browser is
    /// decided by the transport, not here.
    /// </summary>
    private async Task<StagedPageRecord?> AttemptPageAsync(
        RemotePage page,
        int ordinal,
        string jobRoot,
        string referer,
        RemoteChapterManifest manifest,
        CancellationToken cancellationToken)
    {
        var basePath = Path.Combine(jobRoot, "pages", PageFileName(ordinal, page));
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        var rawPath = basePath + ".raw";
        var rawRelative = Path.GetRelativePath(_stagingRoot, rawPath);

        for (var attempt = 0; attempt <= _retryDelays.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                await Task.Delay(_retryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);
            }

            PageFetchResult fetch;
            try
            {
                fetch = await _transport
                    .FetchAsync(page, rawRelative, referer, manifest.RequestHeaders, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                continue;
            }

            if (!fetch.Succeeded || fetch.StoredPath is null)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(fetch.StoredPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            var format = fetch.Format;
            var transformed = false;
            if (page.Transform is not null)
            {
                try
                {
                    var image = await _source
                        .TransformPageAsync(page, bytes, cancellationToken)
                        .ConfigureAwait(false);
                    bytes = image.Bytes;
                    format = image.Format;
                    transformed = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or InvalidOperationException or IOException)
                {
                    // An unknown algorithm or corrupt output is a visible page
                    // failure, never a silent pass-through of scrambled bytes.
                    continue;
                }
            }

            var finalPath = basePath + "." + format;
            var temporary = finalPath + ".part";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, finalPath, overwrite: true);
            }
            catch (IOException)
            {
                continue;
            }
            finally
            {
                TryDelete(temporary);
            }

            TryDelete(rawPath);
            return new StagedPageRecord(
                ordinal,
                page.RemoteKey,
                Path.GetRelativePath(jobRoot, finalPath),
                page.ExpectedBytes,
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)),
                format,
                transformed,
                Validated: true);
        }

        TryDelete(rawPath);
        return null;
    }

    private static bool IsReusable(string absolutePath, StagedPageRecord record)
    {
        if (!File.Exists(absolutePath)) return false;

        try
        {
            if (new FileInfo(absolutePath).Length != record.ObservedBytes) return false;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath)));
            return string.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PageFileName(int ordinal, RemotePage page) =>
        ordinal.ToString("D5") + "-" + Sanitize(page.RemoteKey);

    private static string Sanitize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_');
        }

        return builder.Length == 0 ? "page" : builder.ToString();
    }

    private static void Report(
        IProgress<ChapterDownloadProgress>? progress,
        int completed,
        int total,
        string status) =>
        progress?.Report(new ChapterDownloadProgress(completed, total, status));

    private static JobJournal? ReadJournal(string jobRoot)
    {
        var path = Path.Combine(jobRoot, "job.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<JobJournal>(File.ReadAllText(path));
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged journal trusts nothing: every page is re-validated by
            // hash before reuse anyway.
            return null;
        }
    }

    private static void WriteJournal(
        string jobRoot,
        string manifestHash,
        Dictionary<int, StagedPageRecord> records) =>
        DownloaderJson.WriteAtomic(
            Path.Combine(jobRoot, "job.json"),
            new JobJournal(
                manifestHash,
                [.. records.OrderBy(pair => pair.Key).Select(pair => pair.Value)]));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
