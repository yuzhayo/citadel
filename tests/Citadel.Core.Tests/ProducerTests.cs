using Citadel.Core.Rpl;

namespace Citadel.Core.Tests;

/// <summary>
/// Smoke coverage for the reactive primitives: Producer (cold),
/// DistinctUntilChanged, and
/// BatchOnMain (unexercised upstream-wise; pinned here anyway).
/// </summary>
public class ProducerTests
{
    [Fact]
    public void Producer_IsCold_GeneratorRunsPerSubscription()
    {
        var generations = 0;
        var producer = new Producer<int>(next =>
        {
            generations++;
            next(generations);
            return new Lifetime();
        });

        var owner = new Lifetime();
        var received = new List<int>();

        producer.Subscribe(v => received.Add(v), owner);
        producer.Subscribe(v => received.Add(v), owner);

        Assert.Equal(2, generations);
        Assert.Equal([1, 2], received);
    }

    [Fact]
    public void DistinctUntilChanged_SuppressesConsecutiveDuplicates()
    {
        var stream = new EventStream<int>();
        var producer = Producer<int>.FromStream(stream).DistinctUntilChanged();
        var owner = new Lifetime();
        var received = new List<int>();

        producer.Subscribe(v => received.Add(v), owner);

        stream.Fire(1);
        stream.Fire(1);
        stream.Fire(2);
        stream.Fire(2);
        stream.Fire(1);

        Assert.Equal([1, 2, 1], received);
    }

    [Fact]
    public void BatchOnMain_CoalescesToLatest_PerDrain()
    {
        var main = new FakeMain();
        var stream = new EventStream<int>();
        var producer = Producer<int>.FromStream(stream).BatchOnMain(main.Queue);
        var owner = new Lifetime();
        var received = new List<int>();

        producer.Subscribe(v => received.Add(v), owner);

        stream.Fire(1);
        stream.Fire(2);
        stream.Fire(3); // three values, no drain yet

        main.Pump();
        Assert.Equal(new[] { 3 }, received); // one delivery, latest wins

        stream.Fire(4);
        main.Pump();
        Assert.Equal(new[] { 3, 4 }, received);
    }

    [Fact]
    public void BatchOnMain_DropsQueuedDeliveryAfterOwnerDeath()
    {
        var main = new FakeMain();
        var stream = new EventStream<int>();
        var producer = Producer<int>.FromStream(stream).BatchOnMain(main.Queue);
        var owner = new Lifetime();
        var received = new List<int>();

        producer.Subscribe(received.Add, owner);
        stream.Fire(1);
        owner.Destroy();
        main.Pump();

        Assert.Empty(received);
    }

    [Fact]
    public void OwnerDeath_UnwiresTheSubscription()
    {
        var stream = new EventStream<int>();
        var producer = Producer<int>.FromStream(stream);
        var owner = new Lifetime();
        var count = 0;

        producer.Subscribe(_ => count++, owner);
        stream.Fire(1);
        owner.Destroy();
        stream.Fire(2);

        Assert.Equal(1, count);
    }
}
