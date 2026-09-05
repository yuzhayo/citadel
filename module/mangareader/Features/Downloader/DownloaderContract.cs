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
/// consume: the source registry, the queue contract, the Library root snapshot
/// and the mapping index. Route commands stay on the screens themselves, and no
/// child reaches into a sibling's mutable state through this.
/// </summary>
public sealed class DownloaderContext
{
    internal DownloaderContext(
        MangaSourceRegistry sources,
        DownloadQueueFeature queue,
        LibraryRootContext libraryRoot,
        DownloadSourceIndex index)
    {
        Sources = sources;
        Queue = queue;
        LibraryRoot = libraryRoot;
        Index = index;
    }

    public MangaSourceRegistry Sources { get; }

    public DownloadQueueFeature Queue { get; }

    public LibraryRootContext LibraryRoot { get; }

    public DownloadSourceIndex Index { get; }
}
