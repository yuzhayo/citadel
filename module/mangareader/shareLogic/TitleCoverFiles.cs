using System.IO;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// Pure `cover.*` selection rule for a title folder: files named exactly
/// `cover` plus one extension (`cover.png.&lt;guid&gt;.tmp` drafts and
/// `cover-old.jpg` lookalikes excluded), `cover.png` first, then
/// ordinal-ignore-case. No bitmap work here, so this stays linkable into
/// pure unit tests; `MangaCoverLoader` (bitmap decoding) and the library
/// index (fingerprinting) share this one definition.
/// </summary>
public static class TitleCoverFiles
{
    public const string PreferredTitleCoverFileName = "cover.png";

    public static IEnumerable<string> Enumerate(string titleFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleFolder);

        return Directory
            .EnumerateFiles(titleFolder, "cover.*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                "cover",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => string.Equals(
                Path.GetFileName(path),
                PreferredTitleCoverFileName,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
    }
}
