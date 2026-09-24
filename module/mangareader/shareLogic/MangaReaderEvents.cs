using Module.Mangareader.Archive;
using Module.Mangareader.Library;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// Cross-feature library snapshot. Carries index entries only, never chapter
/// lists: each consumer builds its own presentation and resolves full titles
/// lazily for the titles it actually touches, so no card model, cover state,
/// or chapter enumeration crosses the feature boundary.
/// </summary>
public sealed class LibraryChangedEventArgs : EventArgs
{
    public LibraryChangedEventArgs(IReadOnlyList<LibraryIndexEntry> entries)
    {
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
    }

    public IReadOnlyList<LibraryIndexEntry> Entries { get; }
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

public sealed class CoverBuilderRequestedEventArgs : EventArgs
{
    public CoverBuilderRequestedEventArgs(MangaTitle title) =>
        Title = title ?? throw new ArgumentNullException(nameof(title));

    public MangaTitle Title { get; }
}
