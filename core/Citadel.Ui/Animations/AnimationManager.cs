using System.Windows.Media;
using Citadel.Core.Rpl;

namespace Citadel.Ui.Animations;

/// <summary>
/// One CompositionTarget.Rendering subscription serves every animation and
/// detaches at zero work. Starts and stops requested from a callback are
/// staged until the pass ends, so the active generation is never mutated
/// while it is being visited. The unused upstream short-animation flag is
/// intentionally omitted.
/// </summary>
public sealed class AnimationManager : IDisposable
{
    private readonly IFrameClock _clock;
    private readonly HashSet<Animation> _active = [];
    private readonly HashSet<Animation> _starting = [];
    private readonly HashSet<Animation> _stopping = [];
    private bool _updating;
    private bool _disposed;

    public AnimationManager() : this(new CompositionFrameClock())
    {
    }

    internal AnimationManager(IFrameClock clock) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    internal bool ClockAttached => _clock.Attached;

    internal int ActiveCount => _active.Count + _starting.Count - _stopping.Count;

    public Animation Create(
        TimeSpan duration,
        Action<double> apply,
        Func<double, double>? easing = null,
        Action? completed = null) =>
        new(this, duration, apply, easing ?? Easings.Linear, completed);

    public Animation Start(
        Lifetime lifetime,
        TimeSpan duration,
        Action<double> apply,
        Func<double, double>? easing = null,
        Action? completed = null)
    {
        var animation = Create(duration, apply, easing, completed);
        animation.Start(lifetime);
        return animation;
    }

    internal bool Register(Animation animation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active.Contains(animation) || _starting.Contains(animation)) return false;

        animation.MarkStarted();
        if (_updating)
        {
            _starting.Add(animation);
        }
        else
        {
            _active.Add(animation);
        }
        EnsureClock();
        return true;
    }

    internal void Unregister(Animation animation)
    {
        if (_starting.Remove(animation))
        {
            animation.MarkStopped();
        }
        else if (_updating && _active.Contains(animation))
        {
            _stopping.Add(animation);
        }
        else if (_active.Remove(animation))
        {
            animation.MarkStopped();
        }

        DetachClockIfIdle();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var animation in _active.Concat(_starting))
        {
            animation.MarkStopped();
        }
        _active.Clear();
        _starting.Clear();
        _stopping.Clear();
        if (_clock.Attached) _clock.Detach();
    }

    private void EnsureClock()
    {
        if (!_clock.Attached) _clock.Attach(Tick);
    }

    private void DetachClockIfIdle()
    {
        if (!_updating && _active.Count == 0 && _starting.Count == 0 && _clock.Attached)
        {
            _clock.Detach();
        }
    }

    private void Tick(TimeSpan now)
    {
        if (_disposed) return;

        _updating = true;
        try
        {
            foreach (var animation in _active)
            {
                if (!animation.IsRunning || !animation.Tick(now))
                {
                    _stopping.Add(animation);
                }
            }
        }
        finally
        {
            _updating = false;
        }

        foreach (var animation in _stopping)
        {
            _active.Remove(animation);
            if (!_starting.Contains(animation)) animation.MarkStopped();
        }
        _stopping.Clear();

        foreach (var animation in _starting)
        {
            if (animation.IsRunning) _active.Add(animation);
        }
        _starting.Clear();

        DetachClockIfIdle();
    }
}

internal interface IFrameClock
{
    bool Attached { get; }

    void Attach(Action<TimeSpan> tick);

    void Detach();
}

internal sealed class CompositionFrameClock : IFrameClock
{
    private Action<TimeSpan>? _tick;

    public bool Attached => _tick is not null;

    public void Attach(Action<TimeSpan> tick)
    {
        if (_tick is not null) throw new InvalidOperationException("frame clock already attached");
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        CompositionTarget.Rendering += OnRendering;
    }

    public void Detach()
    {
        if (_tick is null) return;
        CompositionTarget.Rendering -= OnRendering;
        _tick = null;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs rendering) _tick?.Invoke(rendering.RenderingTime);
    }
}
