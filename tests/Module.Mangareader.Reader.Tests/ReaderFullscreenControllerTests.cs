using System.Windows;

namespace Module.Mangareader;

public sealed class ReaderFullscreenControllerTests
{
    [Fact]
    public void Fullscreen_WithoutNativeWindowHandleLeavesStateAndGeometryUntouched()
    {
        WpfTest.Run(() =>
        {
            var window = new Window
            {
                Left = 120,
                Top = 90,
                Width = 700,
                Height = 520,
            };
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            using var controller = new ReaderFullscreenController(window, state, commands);
            controller.Attach(ReaderTestContext.Create(state, commands));
            var before = new Rect(window.Left, window.Top, window.Width, window.Height);

            commands.ToggleFullscreen();

            Assert.False(state.IsFullscreen);
            Assert.Equal(before, new Rect(window.Left, window.Top, window.Width, window.Height));
        });
    }

    [Fact]
    public void Fullscreen_IsNonTopmostAndRestoresExactWindowState()
    {
        WpfTest.Run(() =>
        {
            var window = new Window
            {
                Left = 120,
                Top = 90,
                Width = 700,
                Height = 520,
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                Opacity = 0,
            };
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            var notifications = new ReaderNotificationHub();
            ReaderToastRequestEventArgs? toast = null;
            notifications.ToastRequested += (_, request) => toast = request;
            var context = ReaderTestContext.Create(
                state,
                commands,
                notifications: notifications);
            using var controller = new ReaderFullscreenController(window, state, commands);

            try
            {
                window.Show();
                WpfTest.PumpUntil(() => window.IsLoaded);
                controller.Attach(context);
                var restoreBounds = window.RestoreBounds;
                var style = window.WindowStyle;
                var resizeMode = window.ResizeMode;

                commands.ToggleFullscreen();

                Assert.True(state.IsFullscreen);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.False(window.Topmost);
                Assert.True(window.Width >= restoreBounds.Width);
                Assert.True(window.Height >= restoreBounds.Height);
                Assert.Equal("To exit full screen, press Esc", toast?.Message);
                Assert.Equal(TimeSpan.FromSeconds(2), toast?.Duration);

                commands.ExitFullscreen();

                Assert.False(state.IsFullscreen);
                Assert.Equal(style, window.WindowStyle);
                Assert.Equal(resizeMode, window.ResizeMode);
                Assert.Equal(restoreBounds.Left, window.Left, 3);
                Assert.Equal(restoreBounds.Top, window.Top, 3);
                Assert.Equal(restoreBounds.Width, window.Width, 3);
                Assert.Equal(restoreBounds.Height, window.Height, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DisposingFullscreenFeature_RestoresWindowBeforeDetach()
    {
        WpfTest.Run(() =>
        {
            var window = new Window
            {
                Left = 140,
                Top = 110,
                Width = 640,
                Height = 480,
                ShowInTaskbar = false,
                Opacity = 0,
            };
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            var context = ReaderTestContext.Create(state, commands);
            var controller = new ReaderFullscreenController(window, state, commands);

            try
            {
                window.Show();
                WpfTest.PumpUntil(() => window.IsLoaded);
                controller.Attach(context);
                var restoreBounds = window.RestoreBounds;
                commands.ToggleFullscreen();
                Assert.True(state.IsFullscreen);

                controller.Dispose();

                Assert.False(state.IsFullscreen);
                Assert.Equal(restoreBounds.Left, window.Left, 3);
                Assert.Equal(restoreBounds.Top, window.Top, 3);
                Assert.Equal(restoreBounds.Width, window.Width, 3);
                Assert.Equal(restoreBounds.Height, window.Height, 3);
            }
            finally
            {
                controller.Dispose();
                window.Close();
            }
        });
    }
}
