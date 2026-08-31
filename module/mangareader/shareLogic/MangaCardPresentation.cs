using System.Text;

namespace Module.Mangareader.ShareLogic;

public static class MangaCardPresentation
{
    public static string NormalizeTitle(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "Untitled";

        var result = new StringBuilder(source.Length);
        var separatorPending = false;

        foreach (var character in source)
        {
            if (character == '_' || char.IsWhiteSpace(character))
            {
                separatorPending = result.Length > 0;
                continue;
            }

            if (separatorPending)
            {
                result.Append(' ');
                separatorPending = false;
            }

            result.Append(character);
        }

        return result.Length == 0 ? "Untitled" : result.ToString();
    }
}
