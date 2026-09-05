using Module.Mangareader.Archive;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// Cross-feature library snapshot. Carries domain titles only: each consumer
/// builds its own presentation, so no card model or cover state crosses the
/// feature boundary.
/// </summary>
public sealed class LibraryChangedEventArgs : EventArgs
{
    public LibraryChangedEventArgs(IReadOnlyList<MangaTitle> titles)
    {
        Titles = titles ?? throw new ArgumentNullException(nameof(titles));
    }

    public IReadOnlyList<MangaTitle> Titles { get; }
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
