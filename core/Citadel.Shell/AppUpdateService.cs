using System.Reflection;
using Citadel.Setting;
using Velopack;
using Velopack.Sources;

namespace Citadel.Shell;

/// <summary>
/// Shell-only updater boundary. Settings sees <see cref="AppUpdateState"/>;
/// Velopack and the GitHub source never leak into Setting, Core, or Contract.
/// </summary>
internal interface IAppUpdateService
{
    AppUpdateState Snapshot { get; }

    Task<AppUpdateState> CheckAsync(CancellationToken cancellationToken);

    Task<AppUpdateState> DownloadAsync(
        Action<int> progress,
        CancellationToken cancellationToken);

    bool TryScheduleRestart(out string error);
}

/// <summary>
/// Thin Velopack adapter matching dhepz's user-facing flow: check explicitly,
/// download on demand, then let Velopack replace the installed release and
/// restart the application. It creates no timer or resident worker.
/// </summary>
internal sealed class VelopackUpdateService : IAppUpdateService
{
    internal const string RepositoryUrl = "https://github.com/yuzhayo/citadel";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _availableUpdate;

    public VelopackUpdateService()
    {
        var currentVersion = ProductVersion();
        Snapshot = Unsupported(currentVersion);

        try
        {
            var source = new GithubSource(
                RepositoryUrl,
                accessToken: string.Empty,
                prerelease: false);
            _manager = new UpdateManager(source);

            var installedVersion = _manager.CurrentVersion?.ToString();
            if (!string.IsNullOrWhiteSpace(installedVersion))
            {
                currentVersion = installedVersion;
            }

            var supported = _manager.IsInstalled || _manager.IsPortable;
            Snapshot = new AppUpdateState(
                currentVersion,
                string.Empty,
                supported
                    ? "Ready to check for updates."
                    : "Updates are available after Citadel is installed.",
                0,
                supported,
                supported,
                false,
                false,
                false);
        }
        catch (Exception)
        {
            // An unpackaged development build has no Velopack locator. Keep the
            // Settings card useful and honest, but do not turn that into an app
            // startup failure.
            _manager = null;
        }
    }

    public AppUpdateState Snapshot { get; private set; }

    public async Task<AppUpdateState> CheckAsync(CancellationToken cancellationToken)
    {
        if (_manager is null || !Snapshot.Supported) return Snapshot;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (found is null)
            {
                _availableUpdate = null;
                Snapshot = Snapshot with
                {
                    AvailableVersion = string.Empty,
                    Status = "Citadel is up to date.",
                    Progress = 0,
                    CanCheck = true,
                    CanInstall = false,
                    Busy = false,
                    Available = false,
                };
                return Snapshot;
            }

            _availableUpdate = found;
            var availableVersion = found.TargetFullRelease.Version.ToString();
            Snapshot = Snapshot with
            {
                AvailableVersion = availableVersion,
                Status = $"Version {availableVersion} is available.",
                Progress = 0,
                CanCheck = true,
                CanInstall = true,
                Busy = false,
                Available = true,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Snapshot = Snapshot with
            {
                Status = $"Update check failed: {ErrorText(exception)}",
                CanCheck = true,
                CanInstall = _availableUpdate is not null,
                Busy = false,
            };
        }

        return Snapshot;
    }

    public async Task<AppUpdateState> DownloadAsync(
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_manager is null || _availableUpdate is null) return Snapshot;

        try
        {
            await _manager.DownloadUpdatesAsync(
                _availableUpdate,
                value => progress(Math.Clamp(value, 0, 100)),
                cancellationToken).ConfigureAwait(false);

            Snapshot = Snapshot with
            {
                Status = "Update ready. Restarting Citadel...",
                Progress = 100,
                CanCheck = false,
                CanInstall = false,
                Busy = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Snapshot = Snapshot with
            {
                Status = $"Update failed: {ErrorText(exception)}",
                CanCheck = true,
                CanInstall = true,
                Busy = false,
            };
        }

        return Snapshot;
    }

    public bool TryScheduleRestart(out string error)
    {
        if (_manager is null || _availableUpdate is null)
        {
            error = "No update is ready to install.";
            return false;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(
                _availableUpdate.TargetFullRelease,
                silent: false,
                restart: true,
                restartArgs: []);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = ErrorText(exception);
            return false;
        }
    }

    private static AppUpdateState Unsupported(string currentVersion) => new(
        currentVersion,
        string.Empty,
        "Updates are available after Citadel is installed.",
        0,
        false,
        false,
        false,
        false,
        false);

    private static string ProductVersion()
    {
        var assembly = typeof(VelopackUpdateService).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private static string ErrorText(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
}
