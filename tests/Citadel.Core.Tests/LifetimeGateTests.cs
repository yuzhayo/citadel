using Citadel.Core.Rpl;

namespace Citadel.Core.Tests;

public class LifetimeGateTests
{
    // Gate 5: LIFO destroy — registrations unwind last-in-first-out.
    [Fact]
    public void Destroy_UnwindsLastInFirstOut()
    {
        var lifetime = new Lifetime();
        var order = new List<int>();

        lifetime.Add(() => order.Add(1));
        lifetime.Add(() => order.Add(2));
        lifetime.Add(() => order.Add(3));
        lifetime.Destroy();

        Assert.Equal([3, 2, 1], order);
    }

    [Fact]
    public void Add_AfterDeath_RunsImmediately()
    {
        var lifetime = new Lifetime();
        lifetime.Destroy();

        var ran = false;
        lifetime.Add(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Destroy_IsIdempotent()
    {
        var lifetime = new Lifetime();
        var count = 0;
        lifetime.Add(() => count++);

        lifetime.Destroy();
        lifetime.Destroy();

        Assert.Equal(1, count);
    }

    [Fact]
    public void AddLifetime_ParentOwnsChildDeath_ChildStaysUsable()
    {
        var parent = new Lifetime();
        var child = new Lifetime();
        var order = new List<string>();

        child.Add(() => order.Add("child-first"));
        parent.Add(child);

        // v0 killed the child here. It must stay alive, or every later
        // registration on it fires immediately while the resource is live.
        Assert.True(child.Alive);

        child.Add(() => order.Add("child-second"));
        parent.Add(() => order.Add("parent"));
        parent.Destroy();

        Assert.False(child.Alive);
        Assert.Equal(["parent", "child-second", "child-first"], order);
    }

    /// <summary>
    /// v0's hole: absorbing a child with no callbacks yet left it Alive with
    /// nothing transferred, so anything registered afterwards was orphaned
    /// and never ran at all.
    /// </summary>
    [Fact]
    public void AddLifetime_EmptyChild_StillOwnedByParent()
    {
        var parent = new Lifetime();
        var child = new Lifetime();
        var ran = false;

        parent.Add(child);
        child.Add(() => ran = true);
        parent.Destroy();

        Assert.True(ran);
    }

    [Fact]
    public void AddLifetime_SelfIsIgnored_NoInfiniteRecursion()
    {
        var lifetime = new Lifetime();
        var count = 0;
        lifetime.Add(() => count++);

        lifetime.Add(lifetime);
        lifetime.Destroy();

        Assert.Equal(1, count);
    }

    /// <summary>
    /// Same principle as EventStream's per-subscriber catch: one throwing
    /// cleanup must not strand every cleanup queued behind it.
    /// </summary>
    [Fact]
    public void Destroy_ThrowingCleanup_DoesNotStrandTheRest()
    {
        var lifetime = new Lifetime();
        var ran = new List<string>();

        lifetime.Add(() => ran.Add("first"));
        lifetime.Add(() => throw new InvalidOperationException("boom"));
        lifetime.Add(() => ran.Add("third"));

        lifetime.Destroy();

        Assert.Equal(["third", "first"], ran);
    }

    [Fact]
    public void OperatorPlus_Registers()
    {
        var lifetime = new Lifetime();
        var ran = false;

        _ = lifetime + (() => ran = true);
        lifetime.Destroy();

        Assert.True(ran);
    }
}
