using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class LibraryView : UserControl, IDisposable
{
    private readonly LibraryScanner _scanner = new();
    private readonly MangaCoverLoader _coverLoader = new();
    private readonly ObservableCollection<MangaTitleCardModel> _cards = new();
    private CancellationTokenSource? _scanCancellation;
    private bool _disposed;

    public LibraryView()
    {
        InitializeComponent();
        TitleGrid.ItemsSource = _cards;
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? OpenChapterRequested;

    public event EventHandler<LibraryChangedEventArgs>? TitlesChanged;

    public IReadOnlyList<MangaTitleCardModel> Titles => _cards.ToArray();

    public Task RefreshAsync() => ScanLibraryAsync();

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose manga library folder",
            Multiselect = false,
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true) return;

        LibraryPath.Text = dialog.FolderName;
        await ScanLibraryAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await ScanLibraryAsync();

    private async Task ScanLibraryAsync()
    {
        if (_disposed) return;

        var path = LibraryPath.Text.Trim();
        if (path.Length == 0)
        {
            ShowEmpty(
                "No library selected",
                "Choose the folder that contains one child folder per manga title.");
            StatusText.Text = "A library path is required.";
            return;
        }

        var previous = _scanCancellation;
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        previous?.Cancel();

        ChapterSelector.Dismiss();
        SetBusy(true);
        StatusText.Text = "Scanning title folders...";

        try
        {
            var titles = await _scanner.ScanAsync(path, cancellation.Token);
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            _cards.Clear();
            foreach (var title in titles)
            {
                _cards.Add(new MangaTitleCardModel(title));
            }
            NotifyTitlesChanged();

            if (titles.Count == 0)
            {
                ShowEmpty(
                    "No CBZ titles found",
                    "The selected folder must contain child folders with CBZ files inside them.");
                StatusText.Text = "Scan complete — no CBZ files were found.";
                return;
            }

            EmptyPanel.Visibility = Visibility.Collapsed;
            var chapterCount = titles.Sum(title => title.ChapterCount);
            StatusText.Text = $"{titles.Count} titles · {chapterCount} chapters";
            SetBusy(false);

            await LoadCoversAsync(_cards.ToArray(), cancellation);
            if (!_disposed && ReferenceEquals(_scanCancellation, cancellation))
            {
                NotifyTitlesChanged();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            _cards.Clear();
            NotifyTitlesChanged();
            ShowEmpty("Could not scan the library", exception.GetBaseException().Message);
            StatusText.Text = "Scan failed.";
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                if (!_disposed) SetBusy(false);
            }

            cancellation.Dispose();
        }
    }

    private async Task LoadCoversAsync(
        IReadOnlyList<MangaTitleCardModel> cards,
        CancellationTokenSource scan)
    {
        var options = new ParallelOptions
        {
            CancellationToken = scan.Token,
            MaxDegreeOfParallelism = 4,
        };

        try
        {
            await Parallel.ForEachAsync(cards, options, async (card, cancellationToken) =>
            {
                BitmapSource? cover;
                try
                {
                    cover = await _coverLoader.LoadAsync(
                        card.Manga,
                        maximumPixelWidth: 320,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return;
                }

                if (cover is null || _disposed || !ReferenceEquals(_scanCancellation, scan)) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && ReferenceEquals(_scanCancellation, scan))
                    {
                        card.Cover = cover;
                    }
                });
            });
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
        }
    }

    private void TitleCard_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement { Tag: MangaTitleCardModel card }) return;
        ChapterSelector.ShowTitle(card);
    }

    private void ChapterSelector_ChapterSelected(
        object? sender,
        OpenChapterRequestedEventArgs e)
    {
        ChapterSelector.Dismiss();
        OpenChapterRequested?.Invoke(this, e);
    }

    private void NotifyTitlesChanged() =>
        TitlesChanged?.Invoke(this, new LibraryChangedEventArgs(_cards.ToArray()));

    private void ShowEmpty(string title, string detail)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        BrowseButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
    }

    public void Dispose()
    {
        if (!Dispatcher.CheckAccess())
        {
            _scanCancellation?.Cancel();
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Dispose);
            return;
        }

        if (_disposed) return;
        _disposed = true;
        _scanCancellation?.Cancel();
        _scanCancellation = null;
        ChapterSelector.Dismiss();
        _cards.Clear();
    }
}
