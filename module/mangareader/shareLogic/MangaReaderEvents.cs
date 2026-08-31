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

public sealed class LibraryChangedEventArgs : EventArgs
{
    public LibraryChangedEventArgs(IReadOnlyList<MangaTitleCardModel> titles)
    {
        Titles = titles ?? throw new ArgumentNullException(nameof(titles));
    }

    public IReadOnlyList<MangaTitleCardModel> Titles { get; }
}

public sealed class CoverBakedEventArgs : EventArgs
{
    public CoverBakedEventArgs(MangaTitle title, CoverBakeResult result)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public MangaTitle Title { get; }

    public CoverBakeResult Result { get; }
}
