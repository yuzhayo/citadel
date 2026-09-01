using System.IO;
using System.Security.Cryptography;

namespace Module.Mangareader.Archive;

/// <summary>
/// The promoted latest backup plus structured warnings for stale backups
/// that could not be removed. Cleanup trouble is reported, never swallowed.
/// </summary>
public sealed record BackupPromotion(
    string BackupPath,
    IReadOnlyList<string> CleanupWarnings);

/// <summary>
/// Keeps exactly one latest valid archive backup, globally, under
/// %LocalAppData%\Citadel\MangaReader\cover-backups (tests may point the
/// root elsewhere).
///
/// Rotation is split in two phases so a failed chapter replacement can
/// never consume the previous backup:
///
///   1. <see cref="PrepareBackupCandidate"/> copies the still-unchanged
///      original into a hidden candidate and verifies size, SHA-256 and
///      readability. The candidate stays on disk across the commit point.
///   2. Only after the replacement has committed does
///      <see cref="PromoteCandidate"/> move the candidate to the canonical
///      latest name and remove older backups; cleanup failures become
///      structured warnings instead of silent drops.
///
/// Any failure before promotion leaves the previous valid backup untouched;
/// the transaction discards the orphaned candidate.
/// </summary>
public sealed class LatestCoverBackupStore
{
    public const string LatestBackupBaseName = "latest";

    private readonly string _backupRoot;
    private readonly ArchiveValidator _validator;

    public LatestCoverBackupStore(string? backupRoot = null)
    {
        _backupRoot = backupRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "cover-backups");
        _validator = new ArchiveValidator();
    }

    public string BackupRoot => _backupRoot;

    /// <summary>
    /// Copies the original into a verified candidate backup and returns its
    /// path. The candidate survives until promoted or discarded, so it can
    /// outlive the commit point. The previous latest backup is not touched.
    /// </summary>
    public string PrepareBackupCandidate(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The chapter to back up was not found.", sourcePath);

        Directory.CreateDirectory(_backupRoot);
        var extension = NormalizeBackupExtension(sourcePath);
        var candidatePath = Path.Combine(
            _backupRoot,
            $".candidate-{Guid.NewGuid():N}{extension}");

        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(sourcePath, candidatePath, overwrite: true);
        try
        {
            VerifyCandidate(candidatePath, sourcePath);
            return candidatePath;
        }
        catch
        {
            TryDelete(candidatePath);
            throw;
        }
    }

    /// <summary>
    /// Promotes a verified candidate as the latest backup and then removes
    /// older backups. Removal failures are returned as structured warnings;
    /// they never fail the promotion, because the latest backup is already
    /// in place at that point.
    /// </summary>
    public BackupPromotion PromoteCandidate(string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        if (!File.Exists(candidatePath))
            throw new FileNotFoundException("The backup candidate was not found.", candidatePath);

        var latestPath = Path.Combine(
            _backupRoot,
            LatestBackupBaseName + Path.GetExtension(candidatePath));
        File.Move(candidatePath, latestPath, overwrite: true);
        var warnings = RemoveStaleBackups(latestPath);
        return new BackupPromotion(latestPath, warnings);
    }

    /// <summary>
    /// Removes a candidate that never reached promotion. Best effort: a
    /// leftover hidden candidate is cleaned by the next promotion at the
    /// latest, so a delete failure here must not mask the original error.
    /// </summary>
    public void DiscardCandidate(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath)) return;
        TryDelete(candidatePath);
    }

    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(_backupRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_backupRoot)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void VerifyCandidate(string candidatePath, string sourcePath)
    {
        var source = new FileInfo(sourcePath);
        var candidate = new FileInfo(candidatePath);
        if (candidate.Length != source.Length)
        {
            throw new InvalidDataException(
                "The backup candidate does not match the chapter size.");
        }

        var sourceHash = HashFile(sourcePath);
        var candidateHash = HashFile(candidatePath);
        if (!sourceHash.SequenceEqual(candidateHash))
        {
            throw new InvalidDataException(
                "The backup candidate does not match the chapter content.");
        }

        var validation = _validator.Validate(candidatePath);
        if (validation.State != ArchiveState.Healthy)
        {
            throw new InvalidDataException(
                $"The backup candidate could not be validated: {validation.Detail}");
        }
    }

    private IReadOnlyList<string> RemoveStaleBackups(string latestPath)
    {
        var warnings = new List<string>();
        var latestFullPath = Path.GetFullPath(latestPath);
        foreach (var path in Directory.EnumerateFiles(_backupRoot))
        {
            if (string.Equals(
                    Path.GetFullPath(path),
                    latestFullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                warnings.Add(
                    $"Could not remove stale backup \"{Path.GetFileName(path)}\": {exception.Message}");
            }
        }

        return warnings;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static string NormalizeBackupExtension(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        return extension.Length == 0
            ? ".cbz"
            : extension.ToLowerInvariant();
    }
}
