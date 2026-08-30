using Citadel.Core;
using Citadel.Shell;

namespace Citadel.Uia;

/// <summary>
/// A fake descriptor grows the registry with no
/// filesystem, a background push is safe and deterministic after the drain, and
/// a reserved route is refused visibly.
/// </summary>
public class ModuleGateTests
{
    [Fact]
    public void FakeDescriptor_EntersTheRegistry_WithNoFilesystem()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Gate.Register(Fake.Descriptor("alpha", "Alpha"));
            shell.Main.Pump();

            var snapshot = shell.Gate.Snapshot();
            Assert.Single(snapshot);
            Assert.Equal("alpha", snapshot[0].Route);
        });
    }

    /// <summary>
    /// MainQueue.Post is never inline, so the registry is unchanged when
    /// Register returns even on the main thread. That is the guarded-post
    /// discipline, and a test that asserted otherwise would be pinning a bug.
    /// </summary>
    [Fact]
    public void Register_DefersToTheMainQueue_EvenOnTheMainThread()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Gate.Register(Fake.Descriptor("alpha"));

            Assert.Empty(shell.Gate.Snapshot());
            Assert.True(shell.Main.HasPendingWake);

            shell.Main.Pump();
            Assert.Single(shell.Gate.Snapshot());
        });
    }

    [Fact]
    public void Register_FromBackgroundThread_DoesNotThrow_AndLandsAfterTheDrain()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            var worker = Task.Run(() => shell.Gate.Register(Fake.Descriptor("beta", "Beta")));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(worker.Exception);

            shell.Main.Pump();
            Assert.Single(shell.Gate.Snapshot());
        });
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("settings/appearance")]
    [InlineData("settings/layout")]
    public void ReservedRoute_IsRefused_AndVisibleInFailuresAndLog(string route)
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            RegistrationFailure? observed = null;
            shell.Gate.RegistrationRefused += failure => observed = failure;

            shell.Gate.Register(Fake.Descriptor(route));
            shell.Main.Pump();

            Assert.Empty(shell.Gate.Snapshot());
            Assert.NotNull(observed);
            Assert.Equal(RegistrationRefusal.ReservedRoute, observed.Reason);
            Assert.Contains(route, shell.Gate.Failures().Single().Message);
            Assert.Contains($"'{route}' is a reserved core route", Log.Full());
        });
    }

    [Fact]
    public void DuplicateRoute_KeepsTheFirst_AndRecordsTheSecond()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Gate.Register(Fake.Descriptor("alpha", "First"));
            shell.Gate.Register(Fake.Descriptor("alpha", "Second"));
            shell.Main.Pump();

            var snapshot = shell.Gate.Snapshot();
            Assert.Single(snapshot);
            Assert.Equal("First", snapshot[0].Title);
            Assert.Equal(RegistrationRefusal.DuplicateRoute, shell.Gate.Failures().Single().Reason);
        });
    }

    /// <summary>
    /// Order, then Title, then Route. v0 stopped at Title
    /// (ModuleLoader.cs:58-61), which leaves two entries sharing both free to
    /// swap places between runs — invisible until a sidebar reorders itself.
    /// </summary>
    [Fact]
    public void Registry_OrdersByOrderThenTitleThenRoute()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Gate.Register(Fake.Descriptor("zulu", "Same", order: 10));
            shell.Gate.Register(Fake.Descriptor("alpha", "Same", order: 10));
            shell.Gate.Register(Fake.Descriptor("first", "Aardvark", order: 5));
            shell.Gate.Register(Fake.Descriptor("last", "Zebra", order: 99));
            shell.Main.Pump();

            Assert.Equal(
                ["first", "alpha", "zulu", "last"],
                shell.Gate.Snapshot().Select(d => d.Route));
        });
    }

    [Fact]
    public void Unregister_UnknownRoute_IsRecorded_NotThrown_AndNotAFailure()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();

            shell.Gate.Unregister("never-registered");
            shell.Main.Pump();

            Assert.Empty(shell.Gate.Failures());
            Assert.Contains("unregister 'never-registered' ignored", Log.Full());
        });
    }

    [Fact]
    public void EmptyRegistry_HasNoEntriesAndNoFailures()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness();
            shell.Main.Pump();

            Assert.Empty(shell.Gate.Snapshot());
            Assert.Empty(shell.Gate.Failures());
        });
    }
}
