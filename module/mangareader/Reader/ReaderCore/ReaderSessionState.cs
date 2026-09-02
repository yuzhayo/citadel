using System.ComponentModel;

namespace Module.Mangareader.ReaderCore;

public interface IReaderStateView : INotifyPropertyChanged
{
    double ZoomScale { get; }
    bool IsFullscreen { get; }
    double DimPercent { get; }
    bool IsDrawerOpen { get; }
    bool IsDrawerPinned { get; }
    bool IsAutoScrollRunning { get; }
    double AutoScrollSecondsPerViewport { get; }
    bool IsLoading { get; }
    bool HasError { get; }
    bool IsTransitioning { get; }
}

/// <summary>
/// Observable Reader projection. Features receive <see cref="IReaderStateView"/>
/// only; each owning controller changes state through the internal methods below.
/// </summary>
public sealed class ReaderSessionState : IReaderStateView
{
    private double _zoomScale = ReaderValuePolicy.DefaultZoomScale;
    private bool _isFullscreen;
    private double _dimPercent;
    private bool _isDrawerOpen;
    private bool _isDrawerPinned;
    private bool _isAutoScrollRunning;
    private double _autoScrollSecondsPerViewport = ReaderValuePolicy.DefaultAutoScrollSeconds;
    private bool _isLoading = true;
    private bool _hasError;
    private bool _isTransitioning;

    public ReaderSessionState(
        double dimPercent = ReaderValuePolicy.MinimumDimPercent,
        double autoScrollSecondsPerViewport = ReaderValuePolicy.DefaultAutoScrollSeconds)
    {
        _dimPercent = NormalizeDim(dimPercent);
        _autoScrollSecondsPerViewport = NormalizeAutoScroll(autoScrollSecondsPerViewport);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double ZoomScale => _zoomScale;
    public bool IsFullscreen => _isFullscreen;
    public double DimPercent => _dimPercent;
    public bool IsDrawerOpen => _isDrawerOpen;
    public bool IsDrawerPinned => _isDrawerPinned;
    public bool IsAutoScrollRunning => _isAutoScrollRunning;
    public double AutoScrollSecondsPerViewport => _autoScrollSecondsPerViewport;
    public bool IsLoading => _isLoading;
    public bool HasError => _hasError;
    public bool IsTransitioning => _isTransitioning;

    internal void SetZoomScale(double value) =>
        SetField(ref _zoomScale, NormalizeZoom(value), nameof(ZoomScale));

    internal void SetFullscreen(bool value) =>
        SetField(ref _isFullscreen, value, nameof(IsFullscreen));

    internal void SetDimPercent(double value) =>
        SetField(ref _dimPercent, NormalizeDim(value), nameof(DimPercent));

    internal void SetDrawerOpen(bool value) =>
        SetField(ref _isDrawerOpen, value, nameof(IsDrawerOpen));

    internal void SetDrawerPinned(bool value) =>
        SetField(ref _isDrawerPinned, value, nameof(IsDrawerPinned));

    internal void SetAutoScrollRunning(bool value) =>
        SetField(ref _isAutoScrollRunning, value, nameof(IsAutoScrollRunning));

    internal void SetAutoScrollSecondsPerViewport(double value) =>
        SetField(
            ref _autoScrollSecondsPerViewport,
            NormalizeAutoScroll(value),
            nameof(AutoScrollSecondsPerViewport));

    internal void SetLoading(bool value) =>
        SetField(ref _isLoading, value, nameof(IsLoading));

    internal void SetError(bool value) =>
        SetField(ref _hasError, value, nameof(HasError));

    internal void SetTransitioning(bool value) =>
        SetField(ref _isTransitioning, value, nameof(IsTransitioning));

    public static double NormalizeZoom(double value) => ReaderValuePolicy.NormalizeZoom(value);

    public static double NormalizeDim(double value) => ReaderValuePolicy.NormalizeDim(value);

    public static double NormalizeAutoScroll(double value) =>
        ReaderValuePolicy.NormalizeAutoScroll(value);

    private void SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
