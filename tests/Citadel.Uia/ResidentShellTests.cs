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
            using var resident = new ResidentShell(
                window,
                () => closeSettingsCount++,
                tray,
                () => stopCount++,
                () => shutdownCount++);

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

            tray.RequestExit();

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
                () => throw new InvalidOperationException("shutdown should not be requested"));

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
                () => shutdownCount++);

            resident.PrepareForSessionEnd();
            window.Close();

            Assert.True(resident.ExitRequested);
            Assert.True(tray.Disposed);
            Assert.Equal(1, stopCount);
            Assert.Equal(0, shutdownCount);
            Assert.False(window.IsVisible);
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
