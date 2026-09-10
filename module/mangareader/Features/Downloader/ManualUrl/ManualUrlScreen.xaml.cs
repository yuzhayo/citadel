using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Module.Mangareader.Features.Downloader;

namespace Module.Mangareader.Features.Downloader.ManualUrl;

/// <summary>Manual input route. It owns one URL request and no provider browser controls.</summary>
public partial class ManualUrlScreen : UserControl, IDisposable
{
    private ManualUrlFeature? _feature;
    private CancellationTokenSource? _loadCancellation;
    private bool _disposed;

    public ManualUrlScreen() => InitializeComponent();

    public event EventHandler? BackRequested;

    public event EventHandler<ManualUrlResolution>? TitleResolved;

    public void UseContext(DownloaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed || _feature is not null) return;

        _feature = new ManualUrlFeature(ManualUrlProbeRegistry.Create(context.Sources));
        _feature.StateChanged += Feature_StateChanged;
        Render(_feature.State);
    }

    public void ReportError(string message) => _feature?.ReportError(message);

    private void Feature_StateChanged(object? sender, EventArgs e)
    {
        if (_disposed || _feature is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Feature_StateChanged(sender, e));
            return;
        }

        Render(_feature.State);
    }

    private void UrlField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        LoadButton_Click(sender, e);
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _feature is null || _feature.State.IsBusy) return;

        var previous = _loadCancellation;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        previous?.Cancel();

        try
        {
            await _feature.LoadAsync(UrlField.Text, cancellation.Token);
            if (_disposed || !ReferenceEquals(_loadCancellation, cancellation)) return;
            if (_feature.State.Resolution is { } resolution)
            {
                TitleResolved?.Invoke(this, resolution);
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _loadCancellation?.Cancel();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Render(ManualUrlState state)
    {
        UrlField.Text = state.UrlText;
        UrlField.IsEnabled = !state.IsBusy;
        LoadButton.IsEnabled = !state.IsBusy;
        LoadButton.Content = state.IsBusy ? "Loading…" : "Enter";

        var error = state.ErrorMessage;
        StatusText.Text = error ?? state.StatusMessage ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(StatusText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            error is null ? "Accent" : "Dim");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_feature is not null) _feature.StateChanged -= Feature_StateChanged;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }
}
