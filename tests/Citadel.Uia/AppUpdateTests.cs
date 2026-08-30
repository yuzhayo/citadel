using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Setting;
using Citadel.Shell;

namespace Citadel.Uia;

/// <summary>
/// One focused proof of the Settings-to-Shell updater flow. GitHub itself is
/// not contacted by the suite; the pinned Velopack adapter is the production
/// implementation behind this seam.
/// </summary>
public class AppUpdateTests
{
    [Fact]
    public void CheckThenInstall_PublishesState_AndRequestsRealExit()
    {
        Sta.Run(() =>
        {
            var main = new TestMain();
            var lifetime = new Lifetime();
            var gate = new ModuleGate(main.Queue, lifetime);
            var updates = new StubAppUpdateService();
            var exits = 0;
            var host = new ShellSettingHost(
                gate,
                Fake.Store(),
                () => null,
                updates,
                () => exits++);

            try
            {
                host.CheckForUpdates();

                Assert.Equal(1, updates.Checks);
                Assert.True(host.UpdateState().Available);
                Assert.Equal("0.2.0", host.UpdateState().AvailableVersion);

                host.InstallUpdate();

                Assert.Equal(1, updates.Downloads);
                Assert.Equal(1, updates.Restarts);
                Assert.Equal(1, exits);
                Assert.Equal(100, host.UpdateState().Progress);
            }
            finally
            {
                host.Detach();
                lifetime.Destroy();
            }
        });
    }
}

internal sealed class StubAppUpdateService : IAppUpdateService
{
    public AppUpdateState Snapshot { get; private set; } = new(
        "0.1.0",
        string.Empty,
        "Ready to check for updates.",
        0,
        true,
        true,
        false,
        false,
        false);

    public int Checks { get; private set; }

    public int Downloads { get; private set; }

    public int Restarts { get; private set; }

    public Task<AppUpdateState> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Checks++;
        Snapshot = Snapshot with
        {
            AvailableVersion = "0.2.0",
            Status = "Version 0.2.0 is available.",
            CanCheck = true,
            CanInstall = true,
            Available = true,
        };
        return Task.FromResult(Snapshot);
    }

    public Task<AppUpdateState> DownloadAsync(
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Downloads++;
        progress(50);
        Snapshot = Snapshot with
        {
            Status = "Update ready. Restarting Citadel...",
            Progress = 100,
            CanCheck = false,
            CanInstall = false,
            Busy = false,
        };
        return Task.FromResult(Snapshot);
    }

    public bool TryScheduleRestart(out string error)
    {
        Restarts++;
        error = string.Empty;
        return true;
    }
}
