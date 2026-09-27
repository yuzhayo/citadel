using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>Coalesced 90%-viewport Overlay navigation.</summary>
public sealed class ReaderViewportNavigator : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(180);
    private readonly ReaderFeatureContext _context;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _boundaryCancellation;
    private long _startedAt;
    private double _startOffset;
    private double _targetOffset;
    private bool _disposed;

    public ReaderViewportNavigator(ReaderFeatureContext context)
    {
        _context = context;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            OnTick,
            context.Dispatcher);
        _timer.Stop();
    }

    public async Task StepAsync(int direction)
    {
        if (_disposed || direction == 0) return;
        var state = _context.State;
        if (state.IsLoading || state.HasError || state.IsTransitioning) return;

        _context.Activity.Report(ReaderActivityOrigin.OverlayStep);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Lifetime);
        var previous = Interlocked.Exchange(ref _boundaryCancellation, cancellation);
        previous?.Cancel();
        var token = cancellation.Token;

        try
        {
            if (direction < 0 && _context.Viewport.VerticalOffset <= 0.5)
            {
                if (!await PrepareBoundarySafelyAsync(-1, token)) return;
            }
            else if (direction > 0
                && _context.Viewport.VerticalOffset >= _context.Viewport.ScrollableHeight - 0.5)
            {
                if (!await PrepareBoundarySafelyAsync(1, token)) return;
            }

            if (_disposed || token.IsCancellationRequested) return;
            if (direction < 0 && _context.Chapters.IsAtAbsoluteBeginning) return;
            if (direction > 0 && _context.Chapters.IsAtAbsoluteEnd) return;

            var viewport = _context.Viewport;
            _startOffset = viewport.VerticalOffset;
            _targetOffset = ReaderViewportStepPolicy.NextTarget(
                viewport.VerticalOffset,
                _timer.IsEnabled ? _targetOffset : null,
                viewport.ViewportHeight,
                viewport.ScrollableHeight,
                direction);

            if (!SystemParameters.ClientAreaAnimation)
            {
                viewport.ScrollToVerticalOffset(_targetOffset, ReaderActivityOrigin.OverlayStep);
                return;
            }

            _startedAt = Stopwatch.GetTimestamp();
            if (!_timer.IsEnabled) _timer.Start();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!_disposed && !token.IsCancellationRequested)
        {
            _context.Notifications.ShowToast(
                $"Reader navigation could not continue: {exception.GetBaseException().Message}",
                TimeSpan.FromSeconds(4));
        }
        finally
        {
            Interlocked.CompareExchange(ref _boundaryCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task<bool> PrepareBoundarySafelyAsync(
        int direction,
        CancellationToken cancellationToken)
    {
        try
        {
            await _context.Chapters.PrepareBoundaryAsync(direction, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _context.Notifications.ShowToast(
                $"Adjacent chapter could not be loaded: {exception.GetBaseException().Message}",
                TimeSpan.FromSeconds(4));
            return false;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            _timer.Stop();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startedAt);
        var progress = Math.Clamp(elapsed.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
        var easeOut = 1 - Math.Pow(1 - progress, 3);
        var offset = _startOffset + ((_targetOffset - _startOffset) * easeOut);
        _context.Viewport.ScrollToVerticalOffset(offset, ReaderActivityOrigin.OverlayStep);

        if (progress < 1) return;
        _timer.Stop();
        _context.Viewport.ScrollToVerticalOffset(_targetOffset, ReaderActivityOrigin.OverlayStep);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Interlocked.Exchange(ref _boundaryCancellation, null)?.Cancel();
    }
}
