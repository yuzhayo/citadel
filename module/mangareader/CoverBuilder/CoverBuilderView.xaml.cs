using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Module.Mangareader.Archive;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class CoverBuilderView : UserControl, IDisposable
{
    private readonly CoverBuilderService _service = new();
    private CancellationTokenSource? _operationCancellation;
    private FetchedCoverResult? _fetchedCover;
    private string? _resultStatus;
    private bool _settingLibrary;
    private bool _busy;
    private bool _disposed;

    public CoverBuilderView()
    {
        InitializeComponent();
        SourceField.TextChanged += SourceField_TextChanged;
    }

    public event EventHandler<CoverBakedEventArgs>? CoverBaked;

    public void SetLibrary(IReadOnlyList<MangaTitleCardModel> titles)
    {
        var selectedPath = (TitlePicker.SelectedItem as MangaTitleCardModel)?.FolderPath;
        _settingLibrary = true;
        try
        {
            TitlePicker.ItemsSource = titles;
            TitlePicker.SelectedItem = titles.FirstOrDefault(title => string.Equals(
                title.FolderPath,
                selectedPath,
                StringComparison.OrdinalIgnoreCase))
                ?? titles.FirstOrDefault();
        }
        finally
        {
            _settingLibrary = false;
        }
        UpdateAvailability();
    }

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose cover image",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            Multiselect = false,
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted == true) SourceField.Text = dialog.FileName;
    }

    private void SourceField_TextChanged(string text)
    {
        var source = text.Trim();
        if (_fetchedCover is not null
            && !string.Equals(
                source,
                _fetchedCover.SourceUrl,
                StringComparison.Ordinal))
        {
            _fetchedCover = null;
        }

        _resultStatus = null;
        UpdateAvailability();
    }

    private void TitlePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingLibrary) _resultStatus = null;
        UpdateAvailability();
        e.Handled = true;
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        var sourceUrl = SourceField.Text.Trim();
        if (!CoverSourceLoader.TryGetRemoteUri(sourceUrl, out _))
        {
            StatusText.Text = "Enter a complete HTTP(S) image URL before fetching.";
            return;
        }

        var cancellation = BeginOperation();
        _fetchedCover = null;
        _resultStatus = null;
        StatusText.Text = "Fetching and validating the cover image...";

        try
        {
            var fetched = await _service.FetchAsync(sourceUrl, cancellation.Token);
            if (_disposed || !ReferenceEquals(_operationCancellation, cancellation)) return;
            if (!string.Equals(
                SourceField.Text.Trim(),
                fetched.SourceUrl,
                StringComparison.Ordinal))
            {
                return;
            }

            _fetchedCover = fetched;
            _resultStatus = $"Fetch complete. Saved locally: {fetched.LocalPath}";
            StatusText.Text = _resultStatus;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && ReferenceEquals(_operationCancellation, cancellation))
            {
                _resultStatus = $"Fetch failed: {exception.GetBaseException().Message}";
                StatusText.Text = _resultStatus;
            }
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    private async void BakeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || TitlePicker.SelectedItem is not MangaTitleCardModel selected) return;

        var source = SourceField.Text.Trim();
        if (source.Length == 0)
        {
            StatusText.Text = "Choose a local image or paste an HTTP(S) image URL.";
            return;
        }

        var localPath = source;
        string sourceLabel;
        if (CoverSourceLoader.TryGetRemoteUri(source, out _))
        {
            if (_fetchedCover is null
                || !string.Equals(
                    source,
                    _fetchedCover.SourceUrl,
                    StringComparison.Ordinal))
            {
                StatusText.Text = "Fetch this URL successfully before baking the cover.";
                return;
            }

            localPath = _fetchedCover.LocalPath;
            sourceLabel = _fetchedCover.SourceLabel;
        }
        else
        {
            sourceLabel = Path.GetFileName(localPath);
        }

        var cancellation = BeginOperation();
        _resultStatus = null;
        StatusText.Text = "Detecting the chapter format and rebuilding the first chapter...";

        try
        {
            var result = await _service.BuildFromLocalAsync(
                selected.Manga,
                localPath,
                cancellation.Token);
            if (_disposed || !ReferenceEquals(_operationCancellation, cancellation)) return;

            var bake = result.Bake;
            if (bake.Status != CoverBakeStatus.Baked)
            {
                _resultStatus = $"Bake skipped; the chapter was not changed: {BakeSkippedDetail(bake)}";
                StatusText.Text = _resultStatus;
                return;
            }

            var released = bake.ReleasedProcesses.Count == 0
                ? string.Empty
                : $" Closed {string.Join(", ", bake.ReleasedProcesses.Select(
                    process => $"{process.DisplayName} (PID {process.ProcessId})"))} to release the chapter.";
            var backupDetail = bake.BackupPath is null
                ? " The backup could not be promoted; see the backup warnings."
                : $" Backup: {bake.BackupPath}";
            var warningDetail = bake.BackupWarnings.Count == 0
                ? string.Empty
                : $" Backup warnings: {string.Join(" ", bake.BackupWarnings)}";
            _resultStatus = $"Cover baked from {sourceLabel}.{released}{backupDetail}{warningDetail}";
            StatusText.Text = _resultStatus;
            CoverBaked?.Invoke(
                this,
                new CoverBakedEventArgs(selected.Manga, bake));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && ReferenceEquals(_operationCancellation, cancellation))
            {
                _resultStatus = $"Bake failed; cover was not changed: {exception.GetBaseException().Message}";
                StatusText.Text = _resultStatus;
            }
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    private static string BakeSkippedDetail(CoverBakeResult bake) => bake.Status switch
    {
        CoverBakeStatus.UnsupportedFormat =>
            bake.Detail ?? "this chapter format cannot be rewritten.",
        CoverBakeStatus.CorruptOrTruncated =>
            bake.Detail ?? "the chapter archive looks corrupt or truncated.",
        CoverBakeStatus.UnknownFormat =>
            bake.Detail ?? "the chapter content was not recognized as a supported archive.",
        CoverBakeStatus.MissingSource =>
            "the first chapter file was not found.",
        CoverBakeStatus.Cancelled =>
            "the bake was cancelled before the chapter was replaced.",
        _ => "the bake did not complete.",
    };

    private void UpdateAvailability()
    {
        var hasTitle = TitlePicker.SelectedItem is MangaTitleCardModel;
        var source = SourceField.Text.Trim();
        var isRemote = CoverSourceLoader.TryGetRemoteUri(source, out _);
        var fetchedRemote = isRemote
            && _fetchedCover is not null
            && string.Equals(source, _fetchedCover.SourceUrl, StringComparison.Ordinal)
            && File.Exists(_fetchedCover.LocalPath);
        var sourceReady = source.Length > 0 && (!isRemote || fetchedRemote);

        TitlePicker.IsEnabled = !_busy;
        SourceField.IsEnabled = !_busy;
        BrowseSourceButton.IsEnabled = !_busy;
        FetchButton.IsEnabled = !_busy && isRemote;
        BakeButton.IsEnabled = !_busy && hasTitle && sourceReady;
        StatusText.Text = _resultStatus ?? (hasTitle
            ? isRemote && !fetchedRemote
                ? "Fetch the URL to local storage before baking."
                : "Ready. The earliest chapter will receive the generated cover page."
            : "Scan a Library to load titles.");
    }

    private CancellationTokenSource BeginOperation()
    {
        _operationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _busy = true;
        UpdateAvailability();
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_operationCancellation, cancellation))
        {
            _operationCancellation = null;
            _busy = false;
            if (!_disposed) UpdateAvailability();
        }
        cancellation.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SourceField.TextChanged -= SourceField_TextChanged;
        _operationCancellation?.Cancel();
        _operationCancellation = null;
    }
}
