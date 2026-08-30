namespace Module.Mangareader.Logic;

public sealed record ChapterInfo(string Title, string FilePath);

public sealed record MangaTitle(
    string Title,
    string FolderPath,
    IReadOnlyList<ChapterInfo> Chapters)
{
    public int ChapterCount => Chapters.Count;

    public string ChapterSummary => ChapterCount == 1
        ? "1 chapter"
        : $"{ChapterCount} chapters";
}
