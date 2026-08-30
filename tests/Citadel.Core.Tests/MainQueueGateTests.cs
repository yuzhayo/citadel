using Citadel.Core.Crl;
using Citadel.Core.Rpl;

namespace Citadel.Core.Tests;

/// <summary>
/// A MainQueue test harness: the wake delegate records instead of
/// dispatching, and Pump() plays the role of the main-thread drain.
/// </summary>
internal sealed class FakeMain
{
    public int Wakes;
    private Action? _scheduled;

    public MainQueue Queue { get; }

    public FakeMain(bool isMain = true)
    {
        Queue = new MainQueue(d =>
        {
            Wakes++;
            _scheduled = d;
        }, () => isMain);
    }

    /// <summary>Run the scheduled drain once, if one is pending.</summary>
    public bool Pump()
    {
        var d = _scheduled;
        _scheduled = null;
        if (d is null) return false;
        d();
        return true;
    }
}

public class MainQueueGateTests
{
    // Gate 1: guarded-post drop — post against a destroyed Lifetime never runs.
    [Fact]
    public void GuardedPost_DroppedWhenLifetimeDead()
    {
        var main = new FakeMain();
        var lifetime = new Lifetime();
        var ran = false;

        main.Queue.Post(lifetime, () => ran = true);
        lifetime.Destroy();
        main.Pump();

        Assert.False(ran);
    }

    [Fact]
    public void GuardedPost_RunsWhenLifetimeAlive()
    {
        var main = new FakeMain();
        var lifetime = new Lifetime();
        var ran = false;

        main.Queue.Post(lifetime, () => ran = true);
        main.Pump();

        Assert.True(ran);
    }

    // Gate 2: post-not-inline — Post from the UI thread leaves n == 0
    // immediately after the call returns.
    [Fact]
    public void Post_IsNeverInline_EvenFromMainThread()
    {
        var main = new FakeMain(isMain: true);
        var n = 0;

        main.Queue.Post(() => n++);

        Assert.Equal(0, n);
        main.Pump();
        Assert.Equal(1, n);
    }

    // Gate 3: wake coalescing — N posts produce exactly one wakeup.
    [Fact]
    public void NPosts_CollapseToOneWakeup()
    {
        var main = new FakeMain();

        for (var i = 0; i < 100; i++)
        {
            main.Queue.Post(() => { });
        }

        Assert.Equal(1, main.Wakes);
    }

    [Fact]
    public void WakeIsRearmed_AfterDrainAndNewPost()
    {
        var main = new FakeMain();

        main.Queue.Post(() => { });
        main.Pump();
        Assert.Equal(1, main.Wakes);

        main.Queue.Post(() => { });
        Assert.Equal(2, main.Wakes);
    }

    // Gate 4: post-during-drain defers — a post enqueued while draining
    // runs in the next drain, never recursively.
    [Fact]
    public void PostDuringDrain_RunsInNextDrain_NotRecursively()
    {
        var main = new FakeMain();
        var order = new List<string>();

        main.Queue.Post(() =>
        {
            order.Add("outer");
            main.Queue.Post(() => order.Add("inner"));
        });

        main.Pump();
        Assert.Equal(["outer"], order);
        Assert.Equal(2, main.Wakes); // the inner post earned a fresh wake

        main.Pump();
        Assert.Equal(["outer", "inner"], order);
    }

    [Fact]
    public void FifoOrder_IsAGuarantee()
    {
        var main = new FakeMain();
        var order = new List<int>();

        main.Queue.Post(() => order.Add(1));
        main.Queue.Post(() => order.Add(2));
        main.Queue.Post(() => order.Add(3));
        main.Pump();

        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public void ThrowingAction_DoesNotKillTheDrain()
    {
        var main = new FakeMain();
        var after = false;

        main.Queue.Post(() => throw new InvalidOperationException("boom"));
        main.Queue.Post(() => after = true);
        main.Pump();

        Assert.True(after);
    }

    /// <summary>
    /// The injected wake is Dispatcher.BeginInvoke in production, and it throws
    /// once the dispatcher begins shutting down. If the coalescing flag stayed
    /// set after a failed wake, every later Post would collapse into a wake
    /// that is never coming — a permanent, silent wedge.
    /// </summary>
    [Fact]
    public void FailedWake_DoesNotWedgeTheQueuePermanently()
    {
        var explode = true;
        Action? scheduled = null;
        var queue = new MainQueue(d =>
        {
            if (explode) throw new InvalidOperationException("dispatcher shutting down");
            scheduled = d;
        });

        queue.Post(() => { });        // this wake throws and is swallowed
        Assert.Null(scheduled);

        explode = false;
        var ran = false;
        queue.Post(() => ran = true); // must be able to schedule again

        Assert.NotNull(scheduled);
        scheduled!();
        Assert.True(ran);
    }
}
