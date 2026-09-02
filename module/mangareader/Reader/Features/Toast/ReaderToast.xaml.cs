using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Module.Mangareader;

public sealed partial class ReaderToast : UserControl, IReaderFeature, IReaderVisualFeature
{
    private ReaderFeatureContext? _context;
    private DispatcherTimer? _timer;

    public ReaderToast()
    {
        InitializeComponent();
        Visuals = [new ReaderVisualContribution(ReaderLayer.Toast, this)];
    }

    public string FeatureName => "Toast";
    public IReadOnlyList<ReaderVisualContribution> Visuals { get; }

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        context.Notifications.ToastRequested += OnToastRequested;
    }

    private void OnToastRequested(object? sender, ReaderToastRequestEventArgs e) =>
        Show(e.Message, e.Duration);

    private void Show(string message, TimeSpan duration)
    {
        Message.Text = message;
        Visibility = Visibility.Visible;
        DisposeTimer();
        _timer = new DispatcherTimer(duration, DispatcherPriority.Background, OnDismiss, Dispatcher);
        _timer.Start();
    }

    private void OnDismiss(object? sender, EventArgs e)
    {
        DisposeTimer();
        Visibility = Visibility.Collapsed;
    }

    private void DisposeTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnDismiss;
        _timer = null;
    }

    public void Dispose()
    {
        if (_context is not null)
            _context.Notifications.ToastRequested -= OnToastRequested;
        _context = null;
        DisposeTimer();
        Visibility = Visibility.Collapsed;
    }
}
