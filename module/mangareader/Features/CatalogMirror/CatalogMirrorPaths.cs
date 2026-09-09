using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Module.Mangareader.Features.CatalogMirror;

// Exact path vocabulary under one Catalog root (plan 5.4). This file only
// builds and validates paths: it never reads, writes or deletes files, never
// serializes JSON, and never accepts a provider title as a file name —
// enrichment directories derive from a SHA-256 identity hash instead.
public sealed class CatalogMirrorPaths
{
    private const string EnrichmentFolder = "enrichment";
    private const string DatabaseFileName = "catalog.db";
    private const string MetadataFile = "metadata.json";
    private const string CoverFile = "cover.cache";

    public CatalogMirrorPaths(string root)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Normalized absolute Catalog root.</summary>
    public string Root { get; }

    /// <summary>SQLite catalog database.</summary>
    public string DatabasePath => Combine(DatabaseFileName);

    /// <summary>
    /// Enrichment directory for one normalized title identity. The file name
    /// is a SHA-256 over the identity, never the provider title or path.
    /// </summary>
    public string EnrichmentDirectory(string sourceId, string titleId) =>
        Combine(EnrichmentFolder, HashIdentity(sourceId, titleId));

    public string EnrichmentMetadataPath(string sourceId, string titleId) =>
        Path.Combine(EnrichmentDirectory(sourceId, titleId), MetadataFile);

    public string EnrichmentCoverPath(string sourceId, string titleId) =>
        Path.Combine(EnrichmentDirectory(sourceId, titleId), CoverFile);

    /// <summary>
    /// A same-directory temporary path for atomic replacement. The caller
    /// moves it over the target only after the content is fully flushed.
    /// </summary>
    public string TempPathForAtomicWrite(string targetPath)
    {
        var full = EnsureUnderRoot(targetPath);
        var directory = Path.GetDirectoryName(full)
            ?? throw new CatalogSnapshotException("Path has no directory: " + full);
        return Path.Combine(
            directory,
            "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    /// <summary>
    /// Resolves a candidate against the root and refuses escapes. Every exact
    /// cleanup target passes through here before deletion.
    /// </summary>
    public string EnsureUnderRoot(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var full = Path.GetFullPath(candidate);
        var root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogSnapshotException("Catalog path escapes its root: " + candidate);
        }

        return full;
    }

    /// <summary>One new staging snapshot id. Timestamped and unique.</summary>
    public static string NewSnapshotId() =>
        DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-ffffff")
        + "-"
        + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public static string ValidateSnapshotId(string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        if (snapshotId.Length > 64
            || snapshotId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_'))
        {
            throw new CatalogSnapshotException("Invalid snapshot id: " + snapshotId);
        }

        return snapshotId;
    }

    public static string HashIdentity(string sourceId, string titleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceId + "\0" + titleId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string Combine(params string[] parts)
    {
        var combined = Path.GetFullPath(Path.Combine([Root, .. parts]));
        return EnsureUnderRoot(combined);
    }
}
