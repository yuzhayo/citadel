namespace Module.Mangareader.ShareLogic;

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

    public string FirstChapterTitle => Chapters.Count == 0
        ? "—"
        : Chapters[0].Title;

    public string LatestChapterTitle => Chapters.Count == 0
        ? "—"
        : Chapters[^1].Title;
}
