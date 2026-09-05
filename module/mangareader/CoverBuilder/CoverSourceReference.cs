namespace Module.Mangareader.CoverBuilder;

/// <summary>
/// The typed cover input for one bake operation. Exactly two variants: a local
/// image path, or a remote URL that Cover Builder itself resolves into
/// Citadel-owned storage before touching any archive.
///
/// Callers state which kind of source they have; they do not implement
/// fetch-before-bake. That policy has one owner. Empty values are rejected by
/// the loader that actually reads them.
/// </summary>
public abstract record CoverSourceReference
{
    private CoverSourceReference()
    {
    }

    public sealed record LocalPath(string Path) : CoverSourceReference;

    public sealed record RemoteUrl(string Url) : CoverSourceReference;
}
