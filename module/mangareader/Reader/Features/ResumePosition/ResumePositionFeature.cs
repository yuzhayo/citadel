using System.ComponentModel;
using System.Diagnostics;
using Module.Mangareader.ReaderCore;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Remembers where reading left off per chapter and restores it when the Reader is
/// opened with a resume request. Position is chapter-relative (0..1) so it survives
/// window resize and zoom. Capture is throttled while scrolling plus on chapter
/// change and close; restore runs once, synchronously as the initial load completes
/// (before the loading panel hides), so there is no visible jump.
/// </summary>
public sealed class ResumePositionFeature : IReaderFeature
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);

    private readonly ReadingPositionStore _store;
    private ReaderFeatureContext? _context;
    private long _lastSaveTick;
    private bool _restored;
    private bool _disposed;

    public ResumePositionFeature() : this(ReadingPositionStore.Shared) { }

    internal ResumePositionFeature(ReadingPositionStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public string FeatureName => "ResumePosition";

    public void Attach(ReaderFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        context.Viewport.Changed += OnViewportChanged;
        context.Chapters.ActiveChapterChanged += OnActiveChapterChanged;
        context.State.PropertyChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        var context = _context;
        if (_restored || _disposed || context is null) return;
        if (e.PropertyName != nameof(IReaderStateView.IsLoading)) return;
        if (context.State.IsLoading || context.State.HasError) return;

        _restored = true;
        var progress = context.Content.InitialProgress;
        if (progress is not > 0) return;
        if (!TryActiveBounds(out var top, out var height)) return;

        context.Viewport.ScrollToVerticalOffset(
            top + (Math.Clamp(progress.Value, 0, 1) * height),
            ReaderActivityOrigin.LayoutRestore);
    }

    private void OnViewportChanged(object? sender, ReaderViewportChangedEventArgs e)
    {
        if (_disposed || _context is null) return;
        if (Stopwatch.GetElapsedTime(_lastSaveTick) < SaveInterval) return;
        _lastSaveTick = Stopwatch.GetTimestamp();
        SaveCurrent();
    }

    private void OnActiveChapterChanged(object? sender, OpenChapterRequestedEventArgs e)
    {
        if (_disposed || _context is null) return;
        SaveCurrent();
    }

    private void SaveCurrent()
    {
        var context = _context;
        if (context is null || context.State.IsLoading || context.State.HasError) return;
        if (!TryActiveBounds(out var top, out var height) || height <= 0) return;

        var within = context.Viewport.VerticalOffset - top;
        _store.Set(
            context.Chapters.ActiveChapter.FilePath,
            Math.Clamp(within / height, 0, 1));
    }

    private bool TryActiveBounds(out double top, out double height)
    {
        top = 0;
        height = 0;
        var context = _context;
        if (context is null) return false;

        var activeIndex = context.Chapters.ActiveChapterIndex;
        var zoom = context.State.ZoomScale;
        ChapterSurfaceModel? active = null;
        foreach (var surface in context.Chapters.Surfaces)
        {
            if (surface.ChapterIndex == activeIndex)
            {
                active = surface;
                break;
            }

            top += surface.SurfaceHeight * zoom;
        }

        if (active is null)
        {
            top = 0;
            return false;
        }

        height = active.SurfaceHeight * zoom;
        return height > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var context = _context;
        if (context is not null)
        {
            context.Viewport.Changed -= OnViewportChanged;
            context.Chapters.ActiveChapterChanged -= OnActiveChapterChanged;
            context.State.PropertyChanged -= OnStateChanged;
            SaveCurrent();
        }

        _context = null;
    }
}
