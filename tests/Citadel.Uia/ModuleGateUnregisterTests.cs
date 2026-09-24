using Citadel.Core;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Shell;
using System.Windows;
using System.Windows.Controls;

namespace Citadel.Uia;

/// <summary>
/// Gate 6: staged actual Unregister — release before sidebar removal,
/// RemovalPending + Retry removal on failure, Option A pending replacement,
/// Start blocked while unregistering, ReleaseRoute joining in-flight Stopping.
/// </summary>
public class ModuleGateUnregisterTests
{
    [Fact]
    public void Unregister_WithoutHandler_RemovesImmediately_LegacyPath()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness(withSettingsRoute: false);
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();
            Assert.Single(shell.Gate.Snapshot());

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();

            Assert.Empty(shell.Gate.Snapshot());
            Assert.Empty(shell.Gate.Failures());
            Assert.Contains("unregistered 'alpha'", Log.Full());
        });
    }

    [Fact]
    public void Unregister_WithHandler_KeepsDescriptorUntilReleaseSucceeds()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness(withSettingsRoute: false);
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();

            var releaseGate = new TaskCompletionSource();
            shell.Gate.BindRuntimeReleaseHandler(async _ =>
            {
                await releaseGate.Task;
                return RuntimeReleaseResult.Released;
            });

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();

            // Still registered while release is in flight — sidebar stays.
            Assert.Single(shell.Gate.Snapshot());
            Assert.True(shell.Gate.IsStartBlocked("alpha"));
            Assert.True(shell.Gate.IsRemovalPending("alpha") is false);
            Assert.Contains("unregistering; releasing runtime", Log.Full());

            releaseGate.SetResult();
            DrainUntilIdle(shell);

            Assert.Empty(shell.Gate.Snapshot());
            Assert.False(shell.Gate.IsStartBlocked("alpha"));
            Assert.Contains("unregistered 'alpha'", Log.Full());
        });
    }

    [Fact]
    public void Unregister_WhenReleaseFails_MarksRemovalPending_AndRetrySucceeds()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness(withSettingsRoute: false);
            shell.Gate.Register(Fake.Descriptor("alpha"));
            shell.Main.Pump();

            var calls = 0;
            shell.Gate.BindRuntimeReleaseHandler(_ =>
            {
                calls++;
                return Task.FromResult(calls == 1
                    ? RuntimeReleaseResult.Failed("stop boom")
                    : RuntimeReleaseResult.Released);
            });

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();
            DrainUntilIdle(shell);

            Assert.Single(shell.Gate.Snapshot());
            Assert.True(shell.Gate.IsRemovalPending("alpha"));
            Assert.Equal("stop boom", shell.Gate.RemovalError("alpha"));
            Assert.True(shell.Gate.IsStartBlocked("alpha"));
            Assert.Contains("removal of 'alpha' pending: stop boom", Log.Full());

            shell.Gate.RetryRemoval("alpha");
            shell.Main.Pump();
            DrainUntilIdle(shell);

            Assert.Empty(shell.Gate.Snapshot());
            Assert.False(shell.Gate.IsRemovalPending("alpha"));
            Assert.Equal(2, calls);
        });
    }

    [Fact]
    public void OptionA_RediscoveryDuringUnregister_RegistersPendingReplacementAtomically()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness(withSettingsRoute: false);
            var oldModule = new FakeModule("alpha", _ => new Border());
            shell.Gate.Register(new ModuleDescriptor("alpha", "Alpha", null, 10, oldModule, null));
            shell.Main.Pump();

            var releaseGate = new TaskCompletionSource();
            shell.Gate.BindRuntimeReleaseHandler(async _ =>
            {
                await releaseGate.Task;
                return RuntimeReleaseResult.Released;
            });

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();
            Assert.Single(shell.Gate.Snapshot());

            // Rediscovery while Unregistering: held, not refused as duplicate.
            var newModule = new FakeModule("alpha", _ => new Border());
            shell.Gate.Register(new ModuleDescriptor("alpha", "Alpha v2", null, 10, newModule, null));
            shell.Main.Pump();

            Assert.Empty(shell.Gate.Failures());
            Assert.Single(shell.Gate.Snapshot());
            Assert.Same(oldModule, shell.Gate.Snapshot()[0].Instance);
            Assert.Contains("held as pending replacement", Log.Full());

            releaseGate.SetResult();
            DrainUntilIdle(shell);

            Assert.Single(shell.Gate.Snapshot());
            Assert.Same(newModule, shell.Gate.Snapshot()[0].Instance);
            Assert.Equal("Alpha v2", shell.Gate.Snapshot()[0].Title);
            Assert.False(shell.Gate.IsStartBlocked("alpha"));
            Assert.Contains("re-registered from pending replacement", Log.Full());
        });
    }

    [Fact]
    public async Task Start_WhileUnregistering_Throws()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new LifecycleProbe("alpha");
                shell.Gate.Register(new ModuleDescriptor("alpha", "Alpha", null, 10, module, null));
                shell.Main.Pump();

                var releaseGate = new TaskCompletionSource();
                shell.Gate.BindRuntimeReleaseHandler(async _ =>
                {
                    await releaseGate.Task;
                    return RuntimeReleaseResult.Released;
                });

                shell.Gate.Unregister("alpha");
                shell.Main.Pump();

                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => shell.Coordinator.StartAsync("alpha"));
                Assert.Contains("unregistering", error.Message);

                releaseGate.SetResult();
                DrainUntilIdle(shell);
            });
        });
    }

    [Fact]
    public async Task ReleaseRoute_WhileStopping_JoinsInFlightStop()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new OrderProbe("alpha", failStop: false, hangStop: true);
                shell.Gate.Register(new ModuleDescriptor("alpha", "Alpha", null, 10, module, null));
                shell.Main.Pump();

                await shell.Coordinator.StartAsync("alpha");
                var stop = shell.Coordinator.StopAsync("alpha");
                Assert.Equal(ModuleRuntimeState.Stopping, shell.Coordinator.StateOf("alpha"));

                // Second concurrent Stop path would throw from Stopping; release joins.
                var release = shell.Coordinator.ReleaseRouteAsync("alpha");
                module.ReleaseStop();

                await stop;
                var result = await release;
                Assert.True(result.Success);
                Assert.Equal(ModuleRuntimeState.Stopped, shell.Coordinator.StateOf("alpha"));
            });
        });
    }

    [Fact]
    public async Task ReleaseRoute_StopFails_ReturnsFailure_ForRemovalPending()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new OrderProbe("alpha", failStop: true, hangStop: false);
                shell.Gate.Register(new ModuleDescriptor("alpha", "Alpha", null, 10, module, null));
                shell.Main.Pump();

                await shell.Coordinator.StartAsync("alpha");
                var result = await shell.Coordinator.ReleaseRouteAsync("alpha");

                Assert.False(result.Success);
                Assert.Contains("stop boom", result.Error);
                Assert.Equal(ModuleRuntimeState.Running, shell.Coordinator.StateOf("alpha"));
            });
        });
    }

    [Fact]
    public void FullChain_UnregisterStopFailure_ThenRetryRemoval_Succeeds()
    {
        Sta.Run(() =>
        {
            using var shell = new ShellHarness(withSettingsRoute: false);
            var module = new OrderProbe("alpha", failStop: true, hangStop: false);
            shell.Gate.Register(new ModuleDescriptor("alpha", "Alpha", null, 10, module, null));
            shell.Main.Pump();
            shell.Gate.BindRuntimeReleaseHandler(route => shell.Coordinator.ReleaseRouteAsync(route));

            Run(async () => await shell.Coordinator.StartAsync("alpha"));
            Assert.Equal(ModuleRuntimeState.Running, shell.Coordinator.StateOf("alpha"));

            shell.Gate.Unregister("alpha");
            shell.Main.Pump();
            DrainUntilIdle(shell);

            Assert.Single(shell.Gate.Snapshot());
            Assert.True(shell.Gate.IsRemovalPending("alpha"));
            Assert.Contains("stop boom", shell.Gate.RemovalError("alpha"));
            Assert.False(module.Stopped);

            // Operator fixes the citizen (or Stop succeeds on retry path).
            module.FailStop = false;
            shell.Gate.RetryRemoval("alpha");
            shell.Main.Pump();
            DrainUntilIdle(shell);

            Assert.Empty(shell.Gate.Snapshot());
            Assert.True(module.Stopped);
            Assert.Equal(ModuleRuntimeState.Stopped, shell.Coordinator.StateOf("alpha"));
        });
    }

    private static void DrainUntilIdle(ShellHarness shell)
    {
        for (var i = 0; i < 100; i++)
        {
            shell.Main.Pump();
            if (!shell.Main.HasPendingWake && !shell.HasStagedReleaseWork)
            {
                shell.Main.Pump();
                if (!shell.Main.HasPendingWake && !shell.HasStagedReleaseWork) return;
            }
            Thread.Sleep(10);
        }
    }

    private static void Run(Func<Task> body) =>
        body().GetAwaiter().GetResult();

    private sealed class LifecycleProbe(string route) : IModule, IRuntimeModuleLifecycle
    {
        public string Route { get; } = route;

        public FrameworkElement CreateView(Lifetime lifetime) =>
            new System.Windows.Controls.Border();

        public Task StartRuntimeAsync(
            Lifetime runtimeLifetime,
            RuntimeSession session,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ForceStopRuntime(RuntimeSession session)
        {
        }
    }

    private sealed class OrderProbe(string route, bool failStop, bool hangStop)
        : IModule, IRuntimeModuleLifecycle
    {
        private TaskCompletionSource? _hung;

        public string Route { get; } = route;

        public bool FailStop { get; set; } = failStop;

        public bool Stopped { get; private set; }

        public FrameworkElement CreateView(Lifetime lifetime) =>
            new System.Windows.Controls.Border();

        public Task StartRuntimeAsync(
            Lifetime runtimeLifetime,
            RuntimeSession session,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken)
        {
            if (FailStop)
            {
                throw new InvalidOperationException("stop boom");
            }

            if (hangStop)
            {
                _hung = new TaskCompletionSource();
                using var reg = cancellationToken.Register(() => _hung.TrySetCanceled(cancellationToken));
                await _hung.Task;
            }

            Stopped = true;
        }

        public void ReleaseStop() => _hung?.TrySetResult();

        public void ForceStopRuntime(RuntimeSession session) => _hung?.TrySetResult();
    }
}
