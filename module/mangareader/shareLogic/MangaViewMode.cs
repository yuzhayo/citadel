namespace Module.Mangareader.ShareLogic;

/// <summary>
/// How a title collection is presented. Library and History each own one
/// independent instance of this preference; there is no module-wide mode and no
/// shared singleton, so switching one never switches the other.
/// </summary>
public enum MangaViewMode
{
    Grid,
    List,
}
