using Citadel.Core.Rpl;

namespace Citadel.Core.Tests;

public class EventStreamGateTests
{
    // Gate 6: throwing subscriber doesn't silence the stream — others
    // still receive, and the stream keeps working afterwards.
    [Fact]
    public void ThrowingSubscriber_DoesNotSilenceTheStream()
    {
        var stream = new EventStream<int>();
        var received = new List<string>();

        stream.Subscribe(v => received.Add($"first:{v}"));
        stream.Subscribe(_ => throw new InvalidOperationException("bad subscriber"));
        stream.Subscribe(v => received.Add($"third:{v}"));

        stream.Fire(1);

        Assert.Equal(["first:1", "third:1"], received);
    }

    [Fact]
    public void Stream_KeepsWorking_AfterAThrow()
    {
        var stream = new EventStream<int>();
        var count = 0;

        stream.Subscribe(_ => throw new InvalidOperationException("bad subscriber"));
        stream.Subscribe(_ => count++);

        stream.Fire(1);
        stream.Fire(2);
        stream.Fire(3);

        Assert.Equal(3, count);
    }

    [Fact]
    public void ReturnedLifetime_UnwiresOnDeath()
    {
        var stream = new EventStream<int>();
        var count = 0;

        var sub = stream.Subscribe(_ => count++);
        stream.Fire(1);
        sub.Destroy();
        stream.Fire(2);

        Assert.Equal(1, count);
    }

    [Fact]
    public void OwnerLifetime_UnwiresOnDeath()
    {
        var stream = new EventStream<int>();
        var owner = new Lifetime();
        var count = 0;

        stream.Subscribe(_ => count++, owner);
        stream.Fire(1);
        owner.Destroy();
        stream.Fire(2);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Fire_WithNoSubscribers_IsCheapAndSafe()
    {
        var stream = new EventStream<int>();
        stream.Fire(42);
    }
}
