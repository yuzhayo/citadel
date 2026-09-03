using System.IO;
using Module.Mangareader.Features.Rar;

namespace Module.Mangareader.Archive;

public enum CoverBakeStatus
{
    Baked,
    UnsupportedFormat,
    CorruptOrTruncated,
    UnknownFormat,
    MissingSource,
    Cancelled,
}

public sealed record CoverBakeResult(
    string ChapterPath,
    CoverBakeStatus Status,
    ArchiveFormat Format,
    string? BackupPath,
    string CoverEntryName)
{
    public IReadOnlyList<ReleasedArchiveProcess> ReleasedProcesses { get; init; } =
        Array.Empty<ReleasedArchiveProcess>();

    public string? Detail { get; init; }

    /// <summary>
    /// Structured backup warnings: stale backups that could not be removed,
    /// or a promotion that failed after the chapter was already replaced.
    /// Empty on a clean bake.
    /// </summary>
    public IReadOnlyList<string> BackupWarnings { get; init; } =
        Array.Empty<string>();
}

/// <summary>
/// The transaction-safe cover bake. The source is detected and validated
/// from content, the rewritten archive is built in a unique temporary file
/// beside the source, reopened and verified at byte level, and a verified
/// backup candidate is prepared from the still-unchanged original. Only
/// then is the original atomically replaced, and only after that commit is
/// the candidate promoted to the latest backup — so a failed replacement
/// never consumes the previous backup. Any failure before the replacement
/// leaves the original byte-for-byte unchanged, and once the replacement
/// has committed the result is success even if cancellation is requested
/// late.
/// </summary>
public class ArchiveReplacementTransaction
{
    private readonly LatestCoverBackupStore _backupStore;
    private readonly IArchiveLockCoordinator _lockCoordinator;
    private readonly ArchiveValidator _validator;
    private readonly RarArchiveFeature _rar;

    public ArchiveReplacementTransaction(
        LatestCoverBackupStore? backupStore = null,
        IArchiveLockCoordinator? lockCoordinator = null,
        ArchiveValidator? validator = null,
        RarArchiveFeature? rar = null)
    {
        _backupStore = backupStore ?? new LatestCoverBackupStore();
        _lockCoordinator = lockCoordinator ?? new ArchiveLockCoordinator();
        _validator = validator ?? new ArchiveValidator();
        _rar = rar ?? new RarArchiveFeature();
    }

    public Task<CoverBakeResult> BakeAsync(
        string chapterPath,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        return Task.Run(
            () => Bake(chapterPath, pngBytes, cancellationToken),
            cancellationToken);
    }

    private CoverBakeResult Bake(
        string chapterPath,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterPath);
        if (pngBytes.Length == 0)
            throw new ArgumentException("Cover PNG is empty.", nameof(pngBytes));

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Detect and validate the source archive from its content.
        var validation = _validator.Validate(chapterPath);
        var notBaked = NotBaked(chapterPath, validation);
        if (notBaked is not null) return notBaked;

        // The PNG is validated before any archive mutation begins.
        CoverArchiveWriter.ValidateCoverPng(pngBytes);

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(chapterPath))!,
            $".{Path.GetFileName(chapterPath)}.{Guid.NewGuid():N}.tmp");
        string? backupCandidatePath = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 2. Build the rewritten archive in a unique temporary file
            //    beside the source. The original is still untouched.
            if (validation.Format == ArchiveFormat.Zip)
            {
                var manifest = CoverArchiveWriter.WriteBakedZip(
                    chapterPath,
                    temporaryPath,
                    pngBytes,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                ValidateRewrittenArchive(
                    temporaryPath,
                    pngBytes,
                    manifest,
                    cancellationToken);
            }
            else
            {
                _rar.WriteCover(
                    chapterPath,
                    temporaryPath,
                    pngBytes,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 4. Prepare and verify the backup candidate from the
            //    still-unchanged original. Promotion is deferred until the
            //    replacement commits, so a failed bake never consumes the
            //    previous backup.
            backupCandidatePath = _backupStore.PrepareBackupCandidate(
                chapterPath,
                cancellationToken);

            // 5. Check cancellation immediately before the commit point.
            cancellationToken.ThrowIfCancellationRequested();

            // 6./7. Release conflicting locks, then replace atomically from
            //       the same-volume temporary file.
            var releasedProcesses = CommitReplacement(
                temporaryPath,
                chapterPath,
                cancellationToken);

            // 8. Past the commit point: report success; a late cancellation
            //    must not masquerade as a failed bake. The candidate is
            //    promoted only now.
            var backupPath = PromoteCommittedBackup(
                backupCandidatePath,
                out var backupWarnings);

            return new CoverBakeResult(
                chapterPath,
                CoverBakeStatus.Baked,
                validation.Format,
                backupPath,
                CoverArchiveWriter.GeneratedCoverEntryName)
            {
                ReleasedProcesses = releasedProcesses,
                BackupWarnings = backupWarnings,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Reached only before the commit point returns; the original is
            // unchanged and the previous backup is intact.
            return new CoverBakeResult(
                chapterPath,
                CoverBakeStatus.Cancelled,
                validation.Format,
                null,
                CoverArchiveWriter.GeneratedCoverEntryName);
        }
        finally
        {
            // 9. Always clean the temporary file, and discard a backup
            //    candidate that never reached promotion.
            _backupStore.DiscardCandidate(backupCandidatePath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Promotes the verified candidate now that the replacement has
    /// committed. A promotion failure must not misreport the already
    /// replaced chapter as a failed bake: it becomes a structured warning
    /// and the candidate is discarded.
    /// </summary>
    private string? PromoteCommittedBackup(
        string candidatePath,
        out IReadOnlyList<string> warnings)
    {
        try
        {
            var promotion = _backupStore.PromoteCandidate(candidatePath);
            warnings = promotion.CleanupWarnings;
            return promotion.BackupPath;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            warnings = new[]
            {
                $"The chapter was replaced, but the backup could not be promoted: {exception.Message}",
            };
            return null;
        }
    }

    /// <summary>
    /// Verification seam for the rewritten archive. Failures thrown here
    /// leave the original untouched and the previous backup in place.
    /// </summary>
    internal virtual void ValidateRewrittenArchive(
        string temporaryPath,
        byte[] expectedCoverBytes,
        IReadOnlyList<OriginalEntryManifest> preservedEntries,
        CancellationToken cancellationToken)
    {
        CoverArchiveWriter.ValidateBakedZip(
            temporaryPath,
            expectedCoverBytes,
            preservedEntries,
            cancellationToken);
    }

    /// <summary>
    /// Commit seam: replace the original from the same-volume temporary
    /// file, releasing external locks between attempts. A failure here
    /// leaves the original untouched and the rotated backup in place.
    /// </summary>
    internal virtual IReadOnlyList<ReleasedArchiveProcess> CommitReplacement(
        string temporaryPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var released = new Dictionary<int, ReleasedArchiveProcess>();
        Exception? lastFailure = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
                return released.Values.ToArray();
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                lastFailure = exception;
                foreach (var process in _lockCoordinator.ReleaseForReplacement(
                             targetPath,
                             cancellationToken))
                {
                    released[process.ProcessId] = process;
                }
            }
        }

        throw new IOException(
            "The rebuilt chapter could not replace the original after releasing file locks.",
            lastFailure);
    }

    private static CoverBakeResult? NotBaked(string chapterPath, ArchiveValidation validation) =>
        validation.State switch
        {
            ArchiveState.Healthy when !validation.Capabilities.CoverWritable =>
                new CoverBakeResult(
                    chapterPath,
                    CoverBakeStatus.UnsupportedFormat,
                    validation.Format,
                    null,
                    CoverArchiveWriter.GeneratedCoverEntryName)
                {
                    Detail = validation.Capabilities.Description,
                },
            ArchiveState.CorruptOrTruncated =>
                new CoverBakeResult(
                    chapterPath,
                    CoverBakeStatus.CorruptOrTruncated,
                    validation.Format,
                    null,
                    CoverArchiveWriter.GeneratedCoverEntryName)
                {
                    Detail = validation.Detail,
                },
            ArchiveState.Unknown =>
                new CoverBakeResult(
                    chapterPath,
                    CoverBakeStatus.UnknownFormat,
                    ArchiveFormat.Unknown,
                    null,
                    CoverArchiveWriter.GeneratedCoverEntryName)
                {
                    Detail = validation.Detail,
                },
            ArchiveState.Missing =>
                new CoverBakeResult(
                    chapterPath,
                    CoverBakeStatus.MissingSource,
                    ArchiveFormat.Unknown,
                    null,
                    CoverArchiveWriter.GeneratedCoverEntryName)
                {
                    Detail = validation.Detail,
                },
            _ => null,
        };
}
