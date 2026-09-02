using System.Windows.Controls;

namespace Module.Mangareader;

public sealed partial class ReaderOverlay : UserControl, IReaderFeature, IReaderVisualFeature
{
    private ReaderFeatureContext? _context;
    private ReaderViewportNavigator? _navigator;

    public ReaderOverlay()
    {
        InitializeComponent();
        Visuals = [new ReaderVisualContribution(ReaderLayer.Overlay, this)];
    }

    public string FeatureName => "Overlay";
    public IReadOnlyList<ReaderVisualContribution> Visuals { get; }

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        _navigator = new ReaderViewportNavigator(context);
        context.Input.OverlayClicked += OnOverlayClicked;
    }

    private void OnOverlayClicked(object? sender, ReaderOverlayClickEventArgs e)
    {
        if (_context is null || _context.State.IsLoading
            || _context.State.HasError || _context.State.IsTransitioning)
        {
            return;
        }

        switch (e.Zone)
        {
            case ReaderOverlayZone.Previous:
                _ = _navigator!.StepAsync(-1);
                break;
            case ReaderOverlayZone.Menu:
                _context.Commands.ToggleDrawer();
                break;
            case ReaderOverlayZone.Next:
                _ = _navigator!.StepAsync(1);
                break;
        }
    }

    public void Dispose()
    {
        if (_context is not null) _context.Input.OverlayClicked -= OnOverlayClicked;
        _navigator?.Dispose();
        _navigator = null;
        _context = null;
    }
}
