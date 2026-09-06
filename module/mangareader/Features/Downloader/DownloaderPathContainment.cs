using System.IO;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// One containment rule for the destinations this Downloader writes to.
///
/// A folder name reaches it from a persisted mapping or a persisted queue target
/// and a file name from a persisted job — all of it JSON that a hand edit or a
/// damaged file can change. <see cref="Path.Combine"/> accepts a rooted or a
/// traversing segment without complaint, so an unsafe value would simply point
/// somewhere outside the Library and the write would follow it.
///
/// Feature-local on purpose: these are this feature's own destinations and its own
/// persisted state, not a general path policy for the application. Validation
/// creates nothing, so a folder that does not exist yet is still a valid
/// destination — queueing legitimately happens before the folder is there.
/// </summary>
internal static class DownloaderPathContainment
{
    /// <summary>
    /// Resolves one folder, and optionally one file inside it, under
    /// <paramref name="root"/>. Returns false with a plain-language reason when the
    /// combination cannot be trusted to stay inside that root; the reason is meant
    /// to be shown, not logged and dropped.
    /// </summary>
    /// <param name="fileName">
    /// Null validates the folder alone, which is what a mapping or a cover
    /// destination needs. A persisted job also carries a file name, so it passes one.
    /// </param>
    public static bool TryResolve(
        string? root,
        string? folderName,
        string? fileName,
        out string fullPath,
        out string? problem)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(root))
        {
            problem = "Library root belum diatur.";
            return false;
        }

        // Checked before normalization on purpose: GetFullPath resolves a relative
        // value against the process working directory, so testing the result would
        // always pass and a persisted root like "..\target" would quietly become a
        // destination somewhere else.
        if (!Path.IsPathRooted(root))
        {
            problem = "Library root harus berupa path absolut.";
            return false;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException)
        {
            problem = $"Library root bukan path yang valid: {exception.Message}";
            return false;
        }

        if (!IsSingleSegment(folderName, "folder", out problem)) return false;
        if (fileName is not null && !IsSingleSegment(fileName, "file", out problem)) return false;

        // Combined in two steps: Path.Combine appends a separator for an empty
        // trailing component, which would leave the folder itself looking like a
        // directory prefix of nothing.
        var combined = fileName is null
            ? Path.Combine(normalizedRoot, folderName!)
            : Path.Combine(normalizedRoot, folderName!, fileName);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(combined);
        }
        catch (Exception exception) when (exception is ArgumentException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException)
        {
            problem = $"Path target bukan path yang valid: {exception.Message}";
            return false;
        }

        // GetFullPath has already collapsed every "." and "..", so this is the
        // actual containment verdict rather than a textual guess. OrdinalIgnoreCase
        // is the right comparison on the only filesystem this module runs on.
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"Path target berada di luar Library root: {resolved}";
            return false;
        }

        problem = null;
        fullPath = resolved;
        return true;
    }

    /// <summary>
    /// One path segment and nothing else. A value that is meant to name one folder
    /// or one file may not be able to walk out of the root, so a rooted form, any
    /// separator, a volume separator and the two relative forms are all refused.
    /// </summary>
    private static bool IsSingleSegment(string? value, string role, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = $"Nama {role} target tidak boleh kosong.";
            return false;
        }

        if (Path.IsPathRooted(value))
        {
            problem = $"'{value}' adalah path absolut dan tidak boleh dipakai sebagai nama {role}.";
            return false;
        }

        if (value is "." or "..")
        {
            problem = $"'{value}' bukan nama {role} yang valid.";
            return false;
        }

        if (value.IndexOf(Path.DirectorySeparatorChar) >= 0
            || value.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || value.IndexOf(Path.VolumeSeparatorChar) >= 0)
        {
            problem = $"'{value}' memuat pemisah path dan tidak boleh dipakai sebagai nama {role}.";
            return false;
        }

        return true;
    }
}
