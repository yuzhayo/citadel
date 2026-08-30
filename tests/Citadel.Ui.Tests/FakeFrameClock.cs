using Citadel.Ui.Animations;

namespace Citadel.Ui.Tests;

internal sealed class FakeFrameClock : IFrameClock
{
    private Action<TimeSpan>? _tick;

    public bool Attached => _tick is not null;

    public int AttachCount { get; private set; }

    public int DetachCount { get; private set; }

    public void Attach(Action<TimeSpan> tick)
    {
        Assert.Null(_tick);
        _tick = tick;
        AttachCount++;
    }

    public void Detach()
    {
        Assert.NotNull(_tick);
        _tick = null;
        DetachCount++;
    }

    public void Pulse(double milliseconds)
    {
        var tick = _tick;
        Assert.NotNull(tick);
        tick(TimeSpan.FromMilliseconds(milliseconds));
    }
}
