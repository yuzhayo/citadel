using Citadel.Core.Rpl;
using CrlApi = Citadel.Core.Crl.Crl;

namespace Citadel.Core.Tests;

/// <summary>
/// The static v0-compat surface. Static state lives in Crl, so these
/// tests own it exclusively within this class.
/// </summary>
[Collection("CrlStatics")]
public class CrlShimTests
{
    [Fact]
    public void Post_BeforeInitialize_IsDropped_NotThrown()
    {
        CrlApi.ResetForTests();

        // crl semantics: on_main before init silently discards.
        var ran = false;
        var exception = Record.Exception(() => CrlApi.Post(() => ran = true));

        Assert.Null(exception);
        Assert.False(ran);
    }

    /// <summary>
    /// Discarding is correct; discarding *silently* is not. An App that wires
    /// Crl.Initialize too late would otherwise lose every posted action with
    /// nothing in the log to explain it. Log.Start must come before
    /// Crl.Initialize.
    /// </summary>
    [Fact]
    public void Post_BeforeInitialize_RecordsTheDrop()
    {
        CrlApi.ResetForTests();
        var mark = Log.Full().Length;

        CrlApi.Post(() => { });
        CrlApi.Post(new Lifetime(), () => { });

        var since = Log.Full()[Math.Min(mark, Log.Full().Length)..];
        Assert.Contains("Post before Initialize", since);
        Assert.Contains("guarded Post before Initialize", since);
    }

    [Fact]
    public void Post_ForwardsToMainQueue_NeverInline()
    {
        var main = new FakeMain(isMain: true);
        CrlApi.Initialize(main.Queue);
        var n = 0;

        CrlApi.Post(() => n++);

        Assert.Equal(0, n);
        main.Pump();
        Assert.Equal(1, n);
    }

    [Fact]
    public void GuardedPost_DropsWithDeadLifetime()
    {
        var main = new FakeMain();
        CrlApi.Initialize(main.Queue);
        var lifetime = new Lifetime();
        var ran = false;

        CrlApi.Post(lifetime, () => ran = true);
        lifetime.Destroy();
        main.Pump();

        Assert.False(ran);
    }

    [Fact]
    public void IsMain_DelegatesToTheQueue()
    {
        var main = new FakeMain(isMain: true);
        CrlApi.Initialize(main.Queue);
        Assert.True(CrlApi.IsMain);

        var off = new FakeMain(isMain: false);
        CrlApi.Initialize(off.Queue);
        Assert.False(CrlApi.IsMain);
    }

    [Fact]
#pragma warning disable xUnit1031 // Crl.Run IS a blocking marshal; the waits are the behavior under test
    public void Run_FromOffMain_BlocksUntilDrained()
    {
        var main = new FakeMain(isMain: false);
        CrlApi.Initialize(main.Queue);
        var ran = false;

        var worker = Task.Run(() => CrlApi.Run(() => ran = true));
        Assert.False(worker.Wait(200)); // parked: nothing drained yet

        main.Pump();
        Assert.True(worker.Wait(2000));
        Assert.True(ran);
    }
#pragma warning restore xUnit1031

    [Fact]
    public void Run_OnMain_RunsNow()
    {
        var main = new FakeMain(isMain: true);
        CrlApi.Initialize(main.Queue);
        var ran = false;

        CrlApi.Run(() => ran = true);

        Assert.True(ran);
    }
}
