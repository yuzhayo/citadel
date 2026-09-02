namespace Module.Mangareader.ShareLogic;

public sealed class OpenChapterRequestedEventArgs : EventArgs
{
    public OpenChapterRequestedEventArgs(MangaTitle title, ChapterInfo chapter)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));
    }

    public MangaTitle Title { get; }
    public ChapterInfo Chapter { get; }
}
