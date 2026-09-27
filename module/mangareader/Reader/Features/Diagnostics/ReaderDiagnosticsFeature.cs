using System.IO;
using System.Windows.Media;

namespace Module.Mangareader;

/// <summary>
/// Opt-in Reader diagnostic. When the CITADEL_READER_FPS environment variable
/// is set, it measures the real presented-frame cadence via
/// <see cref="CompositionTarget.Rendering"/> (deduplicated by RenderingTime, since
/// WPF can raise the event more than once per presented frame) and appends a
/// once-per-second fps / average / worst / hitch summary to reader-diagnostics.log.
/// The same line also reports scroll continuity, which separates the two things a
/// reader calls "jitter": a frame-time stall and a viewport that moved somewhere the
/// reader did not ask for.
/// <para>
/// It is inert by default (subscribes to nothing when the flag is unset), so it
/// never changes normal Reader behavior, and it never throws into the render loop.
/// </para>
/// </summary>
public sealed class ReaderDiagnosticsFeature : IReaderFeature
{
    private const string EnableVariable = "CITADEL_READER_FPS";
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly List<double> _frameMilliseconds = [];
    private readonly List<ReaderScrollSample> _scrollSamples = [];
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private DateTime _windowStartedUtc;
    private ReaderFeatureContext? _context;
    private double? _lastVerticalOffset;
    private string? _logPath;
    private bool _rendering;
    private bool _disposed;

    public string FeatureName => "Diagnostics";

    public void Attach(ReaderFeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        if (!IsEnabled()) return;

        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "reader-diagnostics.log");
        _windowStartedUtc = DateTime.UtcNow;
        _lastVerticalOffset = context.Viewport.VerticalOffset;
        Reset();
        SubscribeRendering();
        context.Viewport.Changed += OnViewportChanged;
        Write($"--- diagnostics started (pid {Environment.ProcessId}) ---");
    }

    private static bool IsEnabled()
    {
        var flag = Environment.GetEnvironmentVariable(EnableVariable);
        if (string.IsNullOrWhiteSpace(flag)) return false;
        return !flag.Equals("0", StringComparison.Ordinal)
            && !flag.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private void SubscribeRendering()
    {
        if (_rendering) return;
        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    private void UnsubscribeRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            UnsubscribeRendering();
            return;
        }

        // Count each distinct presented frame once so fps is not over-reported when
        // WPF raises Rendering multiple times for the same frame.
        var renderingTime = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        if (renderingTime == _lastRenderingTime) return;
        if (_lastRenderingTime != TimeSpan.MinValue)
        {
            var ms = (renderingTime - _lastRenderingTime).TotalMilliseconds;
            if (ms > 0 && ms < 1000) _frameMilliseconds.Add(ms);
        }

        _lastRenderingTime = renderingTime;
        if (DateTime.UtcNow - _windowStartedUtc >= Window) Flush();
    }

    private void OnViewportChanged(object? sender, ReaderViewportChangedEventArgs e)
    {
        if (_disposed) return;

        var offset = _context?.Viewport.VerticalOffset;
        if (offset is not { } current || !double.IsFinite(current)) return;
        if (_lastVerticalOffset is not { } previous)
        {
            _lastVerticalOffset = current;
            return;
        }

        _lastVerticalOffset = current;
        if (Math.Abs(current - previous) < 0.0005) return;
        _scrollSamples.Add(new ReaderScrollSample(e.Origin, previous, current));
    }

    private void Flush()
    {
        var summary = ReaderFrameStatsPolicy.Summarize(_frameMilliseconds);
        var scroll = ReaderScrollContinuityPolicy.Summarize(_scrollSamples);
        _frameMilliseconds.Clear();
        _scrollSamples.Clear();
        _windowStartedUtc = DateTime.UtcNow;
        if (summary.Frames == 0) return;
        Write(FormattableString.Invariant(
            $"fps={summary.Fps:F1} avg={summary.AverageFrameMs:F2}ms worst={summary.WorstFrameMs:F2}ms frames={summary.Frames} hitches={summary.Hitches} | scrolls={scroll.Samples} restores={scroll.Restores} back={scroll.Backtracks} worstBack={scroll.WorstBacktrackPx:F1}px fwd={scroll.ForwardJumps} worstFwd={scroll.WorstForwardJumpPx:F1}px"));
    }

    private void Reset()
    {
        if (_logPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.WriteAllText(_logPath, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write(string line)
    {
        if (_logPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A diagnostic must never break the Reader.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_logPath is null) return;
        if (_context is not null) _context.Viewport.Changed -= OnViewportChanged;
        UnsubscribeRendering();
        Flush();
        Write("--- diagnostics stopped ---");
        _context = null;
    }
}
