using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// The narrow context the Catalog child receives. It exposes only what it
/// actually consumes: the source registry, the queue contract, the Library
/// root snapshot, the mapping index and the Auto Cover command. Route commands
/// stay on the screens themselves, and no child reaches into a sibling's
/// mutable state through this.
/// </summary>
public sealed class DownloaderContext
{
    internal DownloaderContext(
        MangaSourceRegistry sources,
        DownloadQueueFeature queue,
        DownloaderPyHostClient browser,
        LibraryRootContext libraryRoot,
        DownloadSourceIndex index,
        ListerFeature lister,
        AutoCoverFeature autoCover)
    {
        Sources = sources;
        Queue = queue;
        Browser = browser;
        LibraryRoot = libraryRoot;
        Index = index;
        Lister = lister;
        AutoCover = autoCover;
    }

    public MangaSourceRegistry Sources { get; }

    public DownloadQueueFeature Queue { get; }

    public DownloaderPyHostClient Browser { get; }

    public LibraryRootContext LibraryRoot { get; }

    public DownloadSourceIndex Index { get; }

    public ListerFeature Lister { get; }

    public AutoCoverFeature AutoCover { get; }
}
