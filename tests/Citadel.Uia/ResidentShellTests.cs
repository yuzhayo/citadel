using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using Citadel.Core;
using Citadel.Core.Crl;
using Citadel.Shell;

namespace Citadel.Uia;

[Collection("Shell power saving serial")]
public class ResidentShellTests : IDisposable
{
    public void Dispose() => PowerSaving.Set(false);

    [Fact]
    public void CloseHidesAndOpenRestoresTheSameWindowViewAndThemeBinding()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            window.ShowInTaskbar = false;
            window.Show();
            var view = window.Router.CurrentView;
            var viewLifetime = window.Router.ViewLifetime;
            var closeSettingsCount = 0;
            var stopCount = 0;
            var shutdownCount = 0;
            var tray = new FakeTrayHost();
            ResidentShell? resident = null;
            resident = new ResidentShell(
                window,
                () => closeSettingsCount++,
                tray,
                () => stopCount++,
                () =>
                {
                    // Successful Exit: App would StopAll, then latch + Shutdown.
                    shutdownCount++;
                    resident!.CompleteExit();
                    return Task.CompletedTask;
                });
            using var _ = resident;

            window.Close();

            Assert.False(window.IsVisible);
            Assert.True(PowerSaving.Enabled);
            Assert.Equal(1, closeSettingsCount);
            Assert.Same(view, window.Router.CurrentView);
            Assert.Same(viewLifetime, window.Router.ViewLifetime);
            Assert.True(viewLifetime!.Alive);

            Assert.True(fixture.Tokens.CommitCore(
                "BgRail",
                JsonValue.Create("#223344")).Applied);
            var hiddenBrush = Assert.IsType<SolidColorBrush>(window.FindResource("BgRail"));
            Assert.Equal(Color.FromRgb(0x22, 0x33, 0x44), hiddenBrush.Color);

            tray.RequestOpen();

            Assert.True(window.IsVisible);
            Assert.False(PowerSaving.Enabled);
            Assert.Same(view, window.Router.CurrentView);
            Assert.Same(viewLifetime, window.Router.ViewLifetime);

            // Tray Exit goes through awaitable BeginExitAsync — wait for it.
            TestAsync.Run(() => resident.RequestExitAsync());

            Assert.True(resident.ExitRequested);
            Assert.True(tray.Disposed);
            Assert.Equal(1, stopCount);
            Assert.Equal(1, shutdownCount);

            window.Close();
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void MissingTrayLeavesNormalCloseBehavior()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            window.ShowInTaskbar = false;
            window.Show();
            var closeSettingsCount = 0;
            var stopCount = 0;
            using var resident = new ResidentShell(
                window,
                () => closeSettingsCount++,
                tray: null,
                () => stopCount++,
                static () => Task.CompletedTask);

            Assert.False(resident.ResidentEnabled);
            window.Close();

            Assert.False(window.IsVisible);
            Assert.Equal(0, closeSettingsCount);
            Assert.Equal(0, stopCount);
        });
    }

    [Fact]
    public void SessionEndBypassesHideWithoutRequestingASecondShutdown()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            window.ShowInTaskbar = false;
            window.Show();
            var tray = new FakeTrayHost();
            var stopCount = 0;
            var shutdownCount = 0;
            using var resident = new ResidentShell(
                window,
                () => { },
                tray,
                () => stopCount++,
                () =>
                {
                    shutdownCount++;
                    return Task.CompletedTask;
                });

            resident.PrepareForSessionEnd();
            window.Close();

            Assert.True(resident.ExitRequested);
            Assert.True(tray.Disposed);
            Assert.Equal(1, stopCount);
            // Session end never calls RequestShutdownAsync — sync latch only.
            Assert.Equal(0, shutdownCount);
            Assert.False(window.IsVisible);
        });
    }

    /// <summary>
    /// Gate 5: failed StopAll must not latch ExitRequested — Exit and Open stay
    /// retryable so the operator can fix the module and Exit again.
    /// </summary>
    [Fact]
    public void FailedShutdown_LeavesExitRetryable_AndDoesNotLatch()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            window.ShowInTaskbar = false;
            window.Show();
            var tray = new FakeTrayHost();
            var shutdownAttempts = 0;
            using var resident = new ResidentShell(
                window,
                () => { },
                tray,
                () => { },
                () =>
                {
                    shutdownAttempts++;
                    // Simulated StopAll failure: App returns without CompleteExit.
                    return Task.CompletedTask;
                });

            TestAsync.Run(() => resident.RequestExitAsync());

            Assert.Equal(1, shutdownAttempts);
            Assert.False(resident.ExitRequested);
            Assert.False(resident.ExitInProgress);
            Assert.False(tray.Disposed);

            // Open still works after a failed Exit.
            tray.RequestOpen();
            Assert.True(window.IsVisible);

            // Retry Exit — second attempt reaches the shutdown callback again.
            TestAsync.Run(() => resident.RequestExitAsync());
            Assert.Equal(2, shutdownAttempts);
            Assert.False(resident.ExitRequested);
        });
    }

    private sealed class FakeTrayHost : ITrayHost
    {
        public event Action? OpenRequested;

        public event Action? ExitRequested;

        internal bool Disposed { get; private set; }

        internal void RequestOpen() => OpenRequested?.Invoke();

        internal void RequestExit() => ExitRequested?.Invoke();

        public void Dispose() => Disposed = true;
    }
}

[CollectionDefinition("Shell power saving serial", DisableParallelization = true)]
public sealed class ShellPowerSavingSerialCollection;
