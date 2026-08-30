using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Module.Mangareader.Logic;

namespace Module.Mangareader;

public partial class ReaderWindow : Window
{
    private const double MaximumPageWidth = 900;
    private readonly MangaTitle _title;
    private readonly ChapterInfo _chapter;
    private readonly CbzChapterLoader _loader = new();
    private readonly CancellationTokenSource _loadCancellation = new();
    private bool _loadStarted;
    private bool _closed;

    public ReaderWindow(MangaTitle title, ChapterInfo chapter)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        _chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));
        InitializeComponent();
        Title = $"{_title.Title} — {_chapter.Title} — Manga Reader";
    }

    private async void ReaderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadStarted) return;
        _loadStarted = true;

        UpdatePageWidth(ReaderScroller.ActualWidth);
        var dpi = VisualTreeHelper.GetDpi(this);
        var decodePixelWidth = Math.Max(1, (int)Math.Ceiling(PageList.Width * dpi.DpiScaleX));
        var progress = new Progress<ChapterLoadProgress>(UpdateProgress);

        try
        {
            var chapter = await _loader.LoadAsync(
                _chapter,
                decodePixelWidth,
                progress,
                _loadCancellation.Token);

            if (_closed || _loadCancellation.IsCancellationRequested) return;

            PageList.ItemsSource = chapter.Pages;
            ReaderScroller.Visibility = Visibility.Visible;
            ReaderScroller.IsEnabled = true;

            // Force one complete measure before exposing the reader. The scroll
            // extent is therefore final when the user drags the thumb.
            ReaderScroller.UpdateLayout();
            LoadingPanel.Visibility = Visibility.Collapsed;
            ReaderScroller.ScrollToTop();
            ReaderScroller.Focus();
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_closed) return;
            ShowError(exception.GetBaseException().Message);
        }
    }

    private void UpdateProgress(ChapterLoadProgress progress)
    {
        if (_closed) return;

        LoadingProgress.Maximum = Math.Max(1, progress.Total);
        LoadingProgress.Value = progress.Loaded;
        LoadingDetail.Text = progress.Loaded == 0
            ? $"{progress.Stage} · {progress.Total} pages"
            : $"{progress.Stage} · {progress.Loaded} / {progress.Total}";
    }

    private void ShowError(string message)
    {
        LoadingTitle.Text = "Could not open chapter";
        LoadingDetail.Text = message;
        LoadingProgress.Visibility = Visibility.Collapsed;
        CloseAfterErrorButton.Visibility = Visibility.Visible;
    }

    private void ReaderScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePageWidth(e.NewSize.Width);

    private void UpdatePageWidth(double availableWidth)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            availableWidth = ActualWidth;
        }

        PageList.Width = Math.Max(1, Math.Min(MaximumPageWidth, availableWidth - 32));
    }

    private void ReaderWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CloseAfterErrorButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ReaderWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
    }
}
