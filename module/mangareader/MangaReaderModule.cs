using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.History;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// MangaReader citizen. Runtime Start constructs the downloader background
/// service on the supplied runtime lifetime; Stop runs the pause-and-drain
/// barrier before the shell may destroy that lifetime; Force Stop aborts the
/// owned process tree without waiting for settlement.
/// </summary>
public sealed class MangaReaderModule : IModule, IRuntimeModuleLifecycle
{
    private readonly ReadingHistory _history = new();
    private readonly LibraryRootContext _libraryRoot = new();
    // The runtime lifetime is the ownership boundary for concurrent proxy
    // reservations. Features retain their own adapters and local quarantine.
    private readonly ProxyLeaseRegistry _proxyReservations = new();
    private DownloaderBackgroundService? _downloader;

    public string Route => "manga-reader";

    public Task StartRuntimeAsync(
        Lifetime runtimeLifetime,
        RuntimeSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeLifetime);
        ArgumentNullException.ThrowIfNull(session);
        if (_downloader is not null)
        {
            // Already running under this module instance (defensive); a clean
            // Stop disposes and nulls the reference before the next Start.
            return Task.CompletedTask;
        }

        var downloader = new DownloaderBackgroundService(_libraryRoot, _proxyReservations);
        _downloader = downloader;
        runtimeLifetime.Add(() =>
        {
            downloader.Dispose();
            if (ReferenceEquals(_downloader, downloader)) _downloader = null;
        });
        return Task.CompletedTask;
    }

    public async Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken)
    {
        var downloader = _downloader
            ?? throw new InvalidOperationException(
                "MangaReader background service is not running.");
        // Success only when neither scheduler has active work; the shell may
        // then destroy the runtime lifetime (which disposes the service).
        await downloader.PauseAndDrainAsync(cancellationToken).ConfigureAwait(true);
    }

    public void ForceStopRuntime(RuntimeSession session)
    {
        // Terminal generation + abort owned children only. Disposal waits are
        // the forced-terminal path on the runtime lifetime.
        _downloader?.ForceAbort(session.Generation);
    }

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        var downloader = _downloader
            ?? throw new InvalidOperationException(
                "MangaReader background service is not started; Start the module runtime first.");
        return new MangaReaderView(
            lifetime,
            _history,
            _libraryRoot,
            downloader.Context,
            _proxyReservations);
    }
}
