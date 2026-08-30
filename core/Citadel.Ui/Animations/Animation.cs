using Citadel.Core.Crl;
using Citadel.Core.Rpl;

namespace Citadel.Ui.Animations;

/// <summary>
/// One lifetime-owned interpolation. Power saving is checked inside Tick,
/// so disabling motion completes on the next frame instead of stopping the
/// manager and preserving a frozen intermediate value.
/// </summary>
public sealed class Animation
{
    private readonly AnimationManager _manager;
    private readonly TimeSpan _duration;
    private readonly Action<double> _apply;
    private readonly Func<double, double> _easing;
    private readonly Action? _completed;
    private TimeSpan? _startedAt;

    internal Animation(
        AnimationManager manager,
        TimeSpan duration,
        Action<double> apply,
        Func<double, double> easing,
        Action? completed)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _manager = manager;
        _duration = duration;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _easing = easing ?? throw new ArgumentNullException(nameof(easing));
        _completed = completed;
    }

    public bool IsRunning { get; private set; }

    public bool Start(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        if (!lifetime.Alive || !_manager.Register(this)) return false;

        lifetime.Add(Stop);
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _manager.Unregister(this);
    }

    internal void MarkStarted()
    {
        _startedAt = null;
        IsRunning = true;
    }

    internal void MarkStopped() => IsRunning = false;

    internal bool Tick(TimeSpan now)
    {
        if (!IsRunning) return false;

        _startedAt ??= now;
        var elapsed = now - _startedAt.Value;
        var progress = PowerSaving.On(PowerSaving.Flags.Animations)
            || _duration == TimeSpan.Zero
            ? 1
            : Math.Clamp(elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);

        _apply(_easing(progress));
        if (!IsRunning) return false;
        if (progress < 1) return true;

        IsRunning = false;
        _completed?.Invoke();
        return false;
    }
}
