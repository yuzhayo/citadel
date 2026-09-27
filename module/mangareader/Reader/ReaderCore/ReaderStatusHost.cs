using System.Windows;
using System.Windows.Controls;
using Citadel.Setting.Components;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.ReaderCore;

/// <summary>
/// The Reader's loading/error overlay. Kept apart from <see cref="FrameContentHost"/>
/// because it drives the visible panel, while the viewport adapter only measures and
/// scrolls. Composition owns the instances; neither type reaches back into the window.
/// </summary>
public sealed class ReaderStatusHost : IReaderStatusHost
{
    private readonly FrameworkElement _content;
    private readonly Border _panel;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly SettingButton _closeButton;
    private readonly ReaderNotificationHub _notifications;

    public ReaderStatusHost(
        FrameworkElement content,
        Border panel,
        TextBlock title,
        TextBlock detail,
        ProgressBar progress,
        SettingButton closeButton,
        ReaderNotificationHub notifications)
    {
        _content = content;
        _panel = panel;
        _title = title;
        _detail = detail;
        _progress = progress;
        _closeButton = closeButton;
        _notifications = notifications;
    }

    public void ShowLoading(string title, string detail)
    {
        _title.Text = title;
        _detail.Text = detail;
        _progress.Visibility = Visibility.Visible;
        _progress.Minimum = 0;
        _progress.Maximum = 1;
        _progress.Value = 0;
        _closeButton.Visibility = Visibility.Collapsed;
        _panel.Visibility = Visibility.Visible;
        // The opaque blocker owns visibility and input. Keep the content in
        // layout so the Reader can derive a real render width before decoding.
        _content.Visibility = Visibility.Visible;
        _content.IsEnabled = false;
    }

    public void ReportProgress(ChapterLoadProgress progress)
    {
        _progress.Maximum = Math.Max(1, progress.Total);
        _progress.Value = progress.Loaded;
        _detail.Text = progress.Loaded == 0
            ? $"{progress.Stage} · {progress.Total} pages"
            : $"{progress.Stage} · {progress.Loaded} / {progress.Total}";
    }

    public void Hide()
    {
        _panel.Visibility = Visibility.Collapsed;
        _content.Visibility = Visibility.Visible;
        _content.IsEnabled = true;
    }

    public void ShowError(string message)
    {
        _title.Text = "Could not open chapter";
        _detail.Text = message;
        _progress.Visibility = Visibility.Collapsed;
        _closeButton.Visibility = Visibility.Visible;
        _panel.Visibility = Visibility.Visible;
        _content.Visibility = Visibility.Visible;
        _content.IsEnabled = false;
    }

    public void SetNonBlockingDetail(string message)
    {
        _detail.Text = message;
        _notifications.ShowToast(message, TimeSpan.FromSeconds(4));
    }
}
