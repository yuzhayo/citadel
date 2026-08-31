using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class CoverBuilderView : UserControl, IDisposable
{
    private readonly CoverBuilderService _service = new();
    private CancellationTokenSource? _operationCancellation;
    private string? _resultStatus;
    private bool _settingLibrary;
    private bool _disposed;

    public CoverBuilderView() => InitializeComponent();

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

    private void TitlePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingLibrary) _resultStatus = null;
        UpdateAvailability();
        e.Handled = true;
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

        _operationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _resultStatus = null;
        SetBusy(true);
        StatusText.Text = "Validating cover and rebuilding the first chapter...";

        try
        {
            var result = await _service.BuildAsync(
                selected.Manga,
                source,
                cancellation.Token);
            if (_disposed || !ReferenceEquals(_operationCancellation, cancellation)) return;

            _resultStatus = $"Cover baked from {result.Source.SourceLabel}. Backup: {result.Bake.BackupPath}";
            StatusText.Text = _resultStatus;
            CoverBaked?.Invoke(
                this,
                new CoverBakedEventArgs(selected.Manga, result.Bake));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && ReferenceEquals(_operationCancellation, cancellation))
            {
                StatusText.Text = $"Cover was not changed: {exception.GetBaseException().Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation = null;
                if (!_disposed) SetBusy(false);
            }
            cancellation.Dispose();
        }
    }

    private void UpdateAvailability()
    {
        var hasTitle = TitlePicker.SelectedItem is MangaTitleCardModel;
        BakeButton.IsEnabled = hasTitle && _operationCancellation is null;
        StatusText.Text = _resultStatus ?? (hasTitle
            ? "Ready. The earliest chapter will receive the generated cover page."
            : "Scan a Library to load titles.");
    }

    private void SetBusy(bool busy)
    {
        TitlePicker.IsEnabled = !busy;
        SourceField.IsEnabled = !busy;
        BrowseSourceButton.IsEnabled = !busy;
        BakeButton.IsEnabled = !busy && TitlePicker.SelectedItem is MangaTitleCardModel;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operationCancellation?.Cancel();
        _operationCancellation = null;
    }
}
