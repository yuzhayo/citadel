using System.Windows;
using System.Windows.Controls;
using Citadel.Setting.Components;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public sealed class FrameContentHostTests
{
    [Fact]
    public void ProgrammaticScroll_PreservesItsTypedOriginAndManualScrollDoesNot()
    {
        WpfTest.Run(() =>
        {
            var items = new ItemsControl
            {
                ItemsSource = Enumerable.Range(0, 20)
                    .Select(_ => new Border { Height = 50 })
                    .ToArray(),
            };
            items.Width = 200;
            items.Height = 1000;
            var scroller = new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = items,
            };
            scroller.Measure(new Size(200, 200));
            scroller.Arrange(new Rect(0, 0, 200, 200));
            scroller.UpdateLayout();
            var activity = new ReaderActivityHub();
            using var host = new FrameContentHost(scroller, items, activity);
            var origins = new List<ReaderActivityOrigin>();
            host.Changed += (_, change) => origins.Add(change.Origin);

            host.ScrollToVerticalOffset(100, ReaderActivityOrigin.AutoScroll);
            scroller.UpdateLayout();
            scroller.ScrollToVerticalOffset(200);
            scroller.UpdateLayout();

            Assert.Equal(
                [ReaderActivityOrigin.AutoScroll, ReaderActivityOrigin.ManualScroll],
                origins);
            Assert.Equal(ReaderActivityOrigin.ManualScroll, activity.LastOrigin);
        });
    }

    [Fact]
    public void StatusHost_ExplicitlyBlocksUnblocksAndSurfacesNonBlockingWarnings()
    {
        WpfTest.Run(() =>
        {
            var content = new Border { Visibility = Visibility.Collapsed, IsEnabled = false };
            var panel = new Border { Visibility = Visibility.Collapsed };
            var title = new TextBlock();
            var detail = new TextBlock();
            var progress = new ProgressBar();
            var close = new SettingButton();
            var notifications = new ReaderNotificationHub();
            string? toast = null;
            notifications.ToastRequested += (_, request) => toast = request.Message;
            var host = new ReaderStatusHost(
                content,
                panel,
                title,
                detail,
                progress,
                close,
                notifications);

            host.ShowLoading("Loading", "Chapter");
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(Visibility.Visible, content.Visibility);
            Assert.False(content.IsEnabled);
            Assert.Equal(Visibility.Visible, progress.Visibility);

            host.ReportProgress(new ShareLogic.ChapterLoadProgress(2, 4, "Decode"));
            Assert.Equal(2, progress.Value);
            Assert.Contains("2 / 4", detail.Text, StringComparison.Ordinal);

            host.Hide();
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.Equal(Visibility.Visible, content.Visibility);
            Assert.True(content.IsEnabled);

            host.SetNonBlockingDetail("Neighbor failed");
            Assert.Equal("Neighbor failed", toast);

            host.ShowError("Bad archive");
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(Visibility.Visible, close.Visibility);
            Assert.False(content.IsEnabled);
        });
    }
}
