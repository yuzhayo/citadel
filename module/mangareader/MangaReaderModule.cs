using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.History;
using Module.Mangareader.Library;

namespace Module.Mangareader;

public sealed class MangaReaderModule : IModule, IResidentModule
{
    private readonly ReadingHistory _history = new();
    private readonly LibraryRootContext _libraryRoot = new();
    private DownloaderBackgroundService? _downloader;

    public string Route => "manga-reader";

    public void AttachApplicationLifetime(Lifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        if (_downloader is not null) return;

        var downloader = new DownloaderBackgroundService(_libraryRoot);
        _downloader = downloader;
        applicationLifetime.Add(() =>
        {
            downloader.Dispose();
            if (ReferenceEquals(_downloader, downloader)) _downloader = null;
        });
    }

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        var downloader = _downloader
            ?? throw new InvalidOperationException(
                "MangaReader background service is not attached to the application lifetime.");
        return new MangaReaderView(
            lifetime,
            _history,
            _libraryRoot,
            downloader.Context);
    }
}
