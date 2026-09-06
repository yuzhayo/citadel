using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// The two routed child screens of the Downloader tab, and nothing else. There
/// is no nested tab control, no third screen and no separate window.
/// </summary>
public enum DownloaderRoute
{
    Catalog,
    DownloadList,
}

/// <summary>
/// The narrow context both children receive. It exposes only what they actually
/// consume: the source registry, the queue contract, the Library root snapshot,
/// the mapping index and the Auto Cover command. Route commands stay on the
/// screens themselves, and no child reaches into a sibling's mutable state
/// through this.
/// </summary>
public sealed class DownloaderContext
{
    internal DownloaderContext(
        MangaSourceRegistry sources,
        DownloadQueueFeature queue,
        LibraryRootContext libraryRoot,
        DownloadSourceIndex index,
        ListerFeature lister,
        AutoCoverFeature autoCover)
    {
        Sources = sources;
        Queue = queue;
        LibraryRoot = libraryRoot;
        Index = index;
        Lister = lister;
        AutoCover = autoCover;
    }

    public MangaSourceRegistry Sources { get; }

    public DownloadQueueFeature Queue { get; }

    public LibraryRootContext LibraryRoot { get; }

    public DownloadSourceIndex Index { get; }

    public ListerFeature Lister { get; }

    public AutoCoverFeature AutoCover { get; }
}
