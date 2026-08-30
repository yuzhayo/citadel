using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using Microsoft.Win32;
using Module.Mangareader.Logic;

namespace Module.Mangareader;

public partial class MangaReaderView : UserControl
{
    private readonly LibraryScanner _scanner = new();
    private CancellationTokenSource? _scanCancellation;
    private ReaderWindow? _readerWindow;
    private bool _disposed;

    public MangaReaderView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        InitializeComponent();
        lifetime.Add(DisposeView);
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose manga library folder",
            Multiselect = false,
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is null
            ? dialog.ShowDialog()
            : dialog.ShowDialog(owner);

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
        previous?.Dispose();

        SetBusy(true);
        StatusText.Text = "Scanning title folders...";

        try
        {
            var titles = await _scanner.ScanAsync(path, cancellation.Token);
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            TitleGrid.ItemsSource = titles;
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
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            TitleGrid.ItemsSource = null;
            ShowEmpty("Could not scan the library", exception.GetBaseException().Message);
            StatusText.Text = "Scan failed.";
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                cancellation.Dispose();
                if (!_disposed) SetBusy(false);
            }
        }
    }

    private void TitleCard_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed
            || sender is not FrameworkElement { Tag: MangaTitle title }
            || title.Chapters.Count == 0)
        {
            return;
        }

        _readerWindow?.Close();

        var reader = new ReaderWindow(title, title.Chapters[0]);
        var owner = Window.GetWindow(this);
        if (owner is not null) reader.Owner = owner;

        reader.Closed += (_, _) =>
        {
            if (ReferenceEquals(_readerWindow, reader)) _readerWindow = null;
        };

        _readerWindow = reader;
        reader.Show();
    }

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

    private void DisposeView()
    {
        if (!Dispatcher.CheckAccess())
        {
            _scanCancellation?.Cancel();
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(DisposeView);
            return;
        }

        if (_disposed) return;
        _disposed = true;

        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = null;

        _readerWindow?.Close();
        _readerWindow = null;
    }
}
