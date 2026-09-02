using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Citadel.Setting.Components;

namespace Module.Mangareader;

public sealed class ReaderInputRouterTests
{
    [Fact]
    public void DrawerOpen_DoesNotCreateASecondFractionBasedInputShield()
    {
        WpfTest.Run(() =>
        {
            var window = new Window();
            var viewport = new TestViewport
            {
                ViewportWidth = 900,
                ViewportHeight = 600,
                PointerPosition = new Point(100, 200),
            };
            var state = new ReaderSessionState();
            state.SetLoading(false);
            state.SetDrawerOpen(true);
            using var router = new ReaderInputRouter(
                window,
                viewport,
                state,
                new ReaderCommandHub(),
                new ReaderActivityHub());
            var zones = new List<ReaderOverlayZone>();
            router.OverlayClicked += (_, click) => zones.Add(click.Zone);

            RaiseLeft(window, UIElement.PreviewMouseLeftButtonDownEvent);
            RaiseLeft(window, UIElement.PreviewMouseLeftButtonUpEvent);

            Assert.Equal([ReaderOverlayZone.Previous], zones);
        });
    }

    [Fact]
    public void BackgroundDragDoesNotBecomeAnOverlayClick()
    {
        WpfTest.Run(() =>
        {
            var window = new Window();
            var viewport = new TestViewport
            {
                ViewportWidth = 900,
                ViewportHeight = 600,
                PointerPosition = new Point(100, 200),
            };
            var state = new ReaderSessionState();
            state.SetLoading(false);
            using var router = new ReaderInputRouter(
                window,
                viewport,
                state,
                new ReaderCommandHub(),
                new ReaderActivityHub());
            var clicks = 0;
            router.OverlayClicked += (_, _) => clicks++;

            RaiseLeft(window, UIElement.PreviewMouseLeftButtonDownEvent);
            viewport.PointerPosition = new Point(
                100 + SystemParameters.MinimumHorizontalDragDistance + 1,
                200);
            RaiseLeft(window, UIElement.PreviewMouseLeftButtonUpEvent);

            Assert.Equal(0, clicks);
        });
    }

    [Fact]
    public void DrawerControls_DoNotPublishManualReaderActivity()
    {
        WpfTest.Run(() =>
        {
            var window = new Window();
            var controls = new StackPanel();
            var button = new SettingButton();
            var slider = new SettingSlider();
            controls.Children.Add(button);
            controls.Children.Add(slider);
            window.Content = controls;
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var activity = new ReaderActivityHub();
            using var router = new ReaderInputRouter(
                window,
                new TestViewport(),
                state,
                new ReaderCommandHub(),
                activity);

            try
            {
                window.Show();
                RaiseLeft(button, UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Null(activity.LastOrigin);
                RaiseLeft(slider, UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Null(activity.LastOrigin);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RaiseLeft(UIElement source, RoutedEvent routedEvent) =>
        source.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = routedEvent,
        });
}
