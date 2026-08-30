using Citadel.Core.Rpl;
using CrlApi = Citadel.Core.Crl.Crl;

namespace Citadel.Core.Tests;

/// <summary>
/// Compatibility traps plus the existing citizen usage shape. This is
/// compile-level proof that compatible source can remain unchanged.
/// </summary>
[Collection("CrlStatics")]
public class VariableTests
{
    // Trap 1: Value must be a settable T, not a producer/stream.
    [Fact]
    public void Value_IsASettableT()
    {
        var heartbeat = new Variable<string>("heartbeat: 0 pulses");

        heartbeat.Value = "heartbeat: 1 pulses";

        Assert.Equal("heartbeat: 1 pulses", heartbeat.Value);
        Assert.Equal("heartbeat: 1 pulses", heartbeat.Current);
    }

    // Trap 2: Subscribe is the two-arg (Action<T>, Lifetime) form.
    [Fact]
    public void Subscribe_TwoArgForm_ReplaysCurrentThenChanges()
    {
        var variable = new Variable<string>("initial");
        var owner = new Lifetime();
        var received = new List<string>();

        variable.Subscribe(text => received.Add(text), owner);
        variable.Value = "second";
        variable.Value = "third";

        Assert.Equal(["initial", "second", "third"], received);

        owner.Destroy();
        variable.Value = "after-death";
        Assert.Equal(3, received.Count);
    }

    [Fact]
    public void Changes_FiresWithoutReplay_OnEverySet()
    {
        var variable = new Variable<int>(5);
        var owner = new Lifetime();
        var received = new List<int>();

        variable.Changes.Subscribe(v => received.Add(v), owner);

        variable.Value = 5;  // v0 fires on every set, equal or not
        variable.Value = 6;
        variable.Value = 6;
        variable.Value = 7;

        Assert.Equal([5, 6, 6, 7], received);
    }

    /// <summary>
    /// The reason Value must not de-duplicate. A list mutated in place has
    /// the same reference, so suppressing equal values would swallow a real
    /// change and leave the screen showing stale rows. Suppression is opt-in
    /// via Operators.DistinctUntilChanged.
    /// </summary>
    [Fact]
    public void Value_FiresEvenWhenTheReferenceIsUnchanged()
    {
        var rows = new List<string> { "a" };
        var variable = new Variable<List<string>>(rows);
        var owner = new Lifetime();
        var fires = 0;

        variable.Changes.Subscribe(_ => fires++, owner);

        rows.Add("b");
        variable.Value = rows;

        Assert.Equal(1, fires);
    }

    /// <summary>
    /// Mirrors GatewayView.OwnLifetime verbatim in shape: Variable ctor
    /// with initial value, two-arg Subscribe whose return is discarded,
    /// CrlApi.Post inside the callback, settable Value.
    /// </summary>
    [Fact]
    public void GatewayUsageShape_StillCompilesAndWorks()
    {
        var main = new FakeMain();
        CrlApi.Initialize(main.Queue);

        var heartbeat = new Variable<string>("heartbeat: 0 pulses");
        var lifetime = new Lifetime();
        string? drawn = null;

        heartbeat.Subscribe(text => CrlApi.Post(lifetime, () =>
        {
            drawn = text;
        }), lifetime);

        var pulses = 1;
        heartbeat.Value = $"heartbeat: {pulses} pulses - lifetime-owned timer, dies on nav-away";

        main.Pump();
        Assert.Equal("heartbeat: 1 pulses - lifetime-owned timer, dies on nav-away", drawn);

        lifetime.Destroy();
        heartbeat.Value = "pulses after nav-away";
        main.Pump();
        Assert.Equal("heartbeat: 1 pulses - lifetime-owned timer, dies on nav-away", drawn);
    }
}
