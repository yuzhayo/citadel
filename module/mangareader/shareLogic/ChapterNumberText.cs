using System.Text.RegularExpressions;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// The one chapter-number comparison rule in MangaReader, shared by Library's
/// Update Checker and the Downloader's Lister because both must answer the same
/// question: does this local archive correspond to that remote chapter number?
///
/// The rule is the last numeric run of a file name or provider number, with
/// leading zeros trimmed, so "0012 - Chapter 12" and "12" agree. It is display
/// and comparison evidence only. Identity is always the provider's chapter key
/// plus its group, never a number, because several groups publish the same number
/// as distinct variants.
/// </summary>
public static partial class ChapterNumberText
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var matches = NumberRun().Matches(raw);
        if (matches.Count == 0) return null;

        var parts = matches[^1].Value.Split('.', 2);
        var integer = parts[0].TrimStart('0');
        if (integer.Length == 0) integer = "0";
        return parts.Length == 2 ? integer + "." + parts[1] : integer;
    }

    /// <summary>Whether two raw numbers refer to the same chapter number.</summary>
    public static bool Equivalent(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        return normalizedLeft is not null
            && string.Equals(normalizedLeft, Normalize(right), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberRun();
}
