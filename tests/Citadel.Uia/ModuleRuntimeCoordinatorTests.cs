using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Shell;

namespace Citadel.Uia;

/// <summary>
/// Gate 1: coordinator Start orders lifecycle/resident before CreateView,
/// never unregisters on Start failure, and keeps ModuleGate eager attach
/// untouched (covered separately in ModuleGateTests).
/// </summary>
public class ModuleRuntimeCoordinatorTests
{
    [Fact]
    public async Task Start_CallsLifecycleBeforeCreateView()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var order = new List<string>();
                var module = new OrderProbeModule("probe", order, lifecycleFirst: true);
                Register(shell, module);
                shell.Main.Pump();

                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                await coordinator.StartAsync("probe");

                Assert.Equal(["lifecycle", "view"], order);
                Assert.Equal(ModuleRuntimeState.Running, coordinator.StateOf("probe"));
            });
        });
    }

    [Fact]
    public async Task Start_ResidentBridge_AttachesRuntimeLifetime_BeforeCreateView()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var order = new List<string>();
                var module = new ResidentBridgeModule("resident", order);
                Register(shell, module);
                shell.Main.Pump();

                // Gate 3: registration never attaches.
                Assert.Empty(order);
                Assert.Null(module.Attached);

                var coordinator = shell.Coordinator;
                // Gate 3: registration never attaches. Start is the only path.
                Assert.Empty(order);

                await coordinator.StartAsync("resident");

                Assert.Equal(["attach", "view"], order);
                Assert.NotNull(module.Attached);
                Assert.NotSame(shell.Lifetime, module.Attached);
                Assert.True(module.Attached!.Alive);
                Assert.Equal(ModuleRuntimeState.Running, coordinator.StateOf("resident"));
            });
        });
    }

    [Fact]
    public async Task Start_Failure_FaultsKeepsDescriptor_DestroysLifetime_NoReject()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new FailingStartModule("broken");
                Register(shell, module);
                shell.Main.Pump();
                RegistrationFailure? refused = null;
                shell.Gate.RegistrationRefused += failure => refused = failure;

                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => coordinator.StartAsync("broken"));

                Assert.Equal(ModuleRuntimeState.Faulted, coordinator.StateOf("broken"));
                Assert.Contains("start boom", coordinator
                    .SlotOrNull("broken")!.LastError);
                Assert.False(module.RuntimeLifetime!.Alive);
                Assert.NotNull(coordinator.SlotOrNull("broken")!.Descriptor);
                Assert.Single(shell.Gate.Snapshot());
                Assert.Null(refused);
                Assert.Empty(shell.Gate.Failures());
            });
        });
    }

    [Fact]
    public async Task Start_RetryAfterFault_BumpsGeneration_NeverReusesIt()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new FlakyStartModule("flaky");
                Register(shell, module);
                shell.Main.Pump();

                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => coordinator.StartAsync("flaky"));
                var first = module.Sessions[0].Generation;
                Assert.Equal(1, first);

                await coordinator.StartAsync("flaky");
                Assert.Equal(ModuleRuntimeState.Running, coordinator.StateOf("flaky"));
                Assert.Equal(2, module.Sessions.Count);
                Assert.Equal(2, module.Sessions[1].Generation);
                Assert.NotEqual(module.Sessions[0].Generation, module.Sessions[1].Generation);
            });
        });
    }

    [Fact]
    public async Task Stop_Success_DestroysRuntimeLifetime_MarksStopped()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new OrderProbeModule("probe", [], lifecycleFirst: true);
                Register(shell, module);
                shell.Main.Pump();

                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                await coordinator.StartAsync("probe");
                var runtime = coordinator.SlotOrNull("probe")!.RuntimeLifetime;
                Assert.NotNull(runtime);

                await coordinator.StopAsync("probe");

                Assert.Equal(ModuleRuntimeState.Stopped, coordinator.StateOf("probe"));
                Assert.False(runtime!.Alive);
                Assert.Null(coordinator.SlotOrNull("probe")!.RuntimeLifetime);
                Assert.Null(coordinator.SlotOrNull("probe")!.LastError);
            });
        });
    }

    [Fact]
    public async Task Stop_Failure_ReturnsRunning_RetainsLifetime_RecordsError()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new OrderProbeModule("probe", [], lifecycleFirst: true);
                module.FailStop = true;
                Register(shell, module);
                shell.Main.Pump();

                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                await coordinator.StartAsync("probe");
                var runtime = coordinator.SlotOrNull("probe")!.RuntimeLifetime;

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => coordinator.StopAsync("probe"));

                Assert.Equal(ModuleRuntimeState.Running, coordinator.StateOf("probe"));
                Assert.True(runtime!.Alive);
                Assert.NotNull(coordinator.SlotOrNull("probe")!.RuntimeLifetime);
                Assert.Contains("stop boom",
                    coordinator.SlotOrNull("probe")!.LastError);
                Assert.Single(shell.Gate.Snapshot());
            });
        });
    }

    [Fact]
    public async Task Stop_WhileStarting_QueuesPendingStop_RunsAfterStart()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var gate = new TaskCompletionSource();
                var module = new BlockingStartModule("slow", gate.Task);
                Register(shell, module);
                shell.Main.Pump();

                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                var start = coordinator.StartAsync("slow");

                // Start is blocked inside StartRuntimeAsync; Stop records PendingStop.
                Assert.Equal(ModuleRuntimeState.Starting, coordinator.StateOf("slow"));
                await coordinator.StopAsync("slow");
                Assert.Equal(ModuleRuntimeState.Starting, coordinator.StateOf("slow"));

                gate.SetResult();
                await start;

                Assert.Equal(ModuleRuntimeState.Stopped, coordinator.StateOf("slow"));
                // Successful Stop clears the runtime lifetime (destroy ran).
                Assert.Null(coordinator.SlotOrNull("slow")!.RuntimeLifetime);
                Assert.Null(coordinator.SlotOrNull("slow")!.RuntimeView);
            });
        });
    }

    [Fact]
    public async Task ReleaseRoute_WhenStoppedOrFaulted_IsReleasedWithoutStop()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var module = new FailingStartModule("broken");
                Register(shell, module);
                shell.Main.Pump();

                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => coordinator.StartAsync("broken"));

                var result = await coordinator.ReleaseRouteAsync("broken");
                Assert.True(result.Success);
                Assert.Null(result.Error);
                Assert.Equal(ModuleRuntimeState.Faulted, coordinator.StateOf("broken"));
                Assert.Single(shell.Gate.Snapshot());
            });
        });
    }

    [Fact]
    public async Task ReleaseRoute_UnknownRoute_IsReleased()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                var result = await coordinator.ReleaseRouteAsync("never");
                Assert.True(result.Success);
            });
        });
    }

    [Fact]
    public async Task Start_UnknownRoute_Throws_AndLeavesNoSlot()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var coordinator = new ModuleRuntimeCoordinator(shell.Gate, shell.Tokens);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => coordinator.StartAsync("missing"));
                Assert.Null(coordinator.SlotOrNull("missing"));
                Assert.Equal(ModuleRuntimeState.Stopped, coordinator.StateOf("missing"));
            });
        });
    }

    [Fact]
    public async Task StopAll_StopsEveryRunningRoute_Parallel_NonTransactional()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var good = new OrderProbeModule("good", [], lifecycleFirst: true);
                var bad = new OrderProbeModule("bad", [], lifecycleFirst: true);
                bad.FailStop = true;
                Register(shell, good);
                Register(shell, bad);
                shell.Main.Pump();

                var coordinator = shell.Coordinator;
                await coordinator.StartAsync("good");
                await coordinator.StartAsync("bad");
                Assert.Equal(ModuleRuntimeState.Running, coordinator.StateOf("good"));
                Assert.Equal(ModuleRuntimeState.Running, coordinator.StateOf("bad"));

                var failures = await coordinator.StopAllAsync();

                // Non-transactional: good stays stopped even though bad failed.
                Assert.Equal(ModuleRuntimeState.Stopped, coordinator.StateOf("good"));
                Assert.Equal(ModuleRuntimeState.Running, coordinator.StateOf("bad"));
                Assert.NotNull(coordinator.SlotOrNull("bad")!.RuntimeLifetime);
                var failure = Assert.Single(failures);
                Assert.StartsWith("bad:", failure, StringComparison.Ordinal);
                Assert.Contains("stop boom", failure, StringComparison.Ordinal);
            });
        });
    }

    [Fact]
    public async Task StopAll_SkipsStoppedAndFaulted_Routes()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var live = new OrderProbeModule("live", [], lifecycleFirst: true);
                var broken = new FailingStartModule("broken");
                Register(shell, live);
                Register(shell, broken);
                shell.Main.Pump();

                var coordinator = shell.Coordinator;
                await coordinator.StartAsync("live");
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => coordinator.StartAsync("broken"));

                var failures = await coordinator.StopAllAsync();

                Assert.Equal([], failures);
                Assert.Equal(ModuleRuntimeState.Stopped, coordinator.StateOf("live"));
                Assert.Equal(ModuleRuntimeState.Faulted, coordinator.StateOf("broken"));
                // Faulted already destroyed its lifetime on Start failure.
                Assert.Null(coordinator.SlotOrNull("broken")!.RuntimeLifetime);
            });
        });
    }

    [Fact]
    public async Task StopAll_WhileStarting_JoinsPendingStop_OrReportsFailure()
    {
        Sta.Run(() =>
        {
            Run(async () =>
            {
                using var shell = new ShellHarness(withSettingsRoute: false);
                var gate = new TaskCompletionSource();
                var module = new BlockingStartModule("slow", gate.Task);
                Register(shell, module);
                shell.Main.Pump();

                var coordinator = shell.Coordinator;
                var start = coordinator.StartAsync("slow");
                Assert.Equal(ModuleRuntimeState.Starting, coordinator.StateOf("slow"));

                // StopAll records PendingStop without a second concurrent Stop.
                var stopAll = coordinator.StopAllAsync();
                Assert.True(coordinator.SlotOrNull("slow")!.PendingStop);

                gate.SetResult();
                await start;
                var failures = await stopAll;

                Assert.Equal([], failures);
                Assert.Equal(ModuleRuntimeState.Stopped, coordinator.StateOf("slow"));
                Assert.Null(coordinator.SlotOrNull("slow")!.RuntimeLifetime);
            });
        });
    }

    /// <summary>
    /// Completing async work without a DispatcherSynchronizationContext only
    /// stays on the STA thread when the fakes complete synchronously. Blocking
    /// wait here keeps CreateView on the thread Sta.Run started.
    /// </summary>
    private static void Run(Func<Task> body) =>
        body().GetAwaiter().GetResult();

    private static void Register(ShellHarness shell, IModule module) =>
        shell.Gate.Register(new ModuleDescriptor(
            module.Route, module.Route, null, 10, module, null));

    private sealed class OrderProbeModule(
        string route,
        List<string> order,
        bool lifecycleFirst) : IModule, IRuntimeModuleLifecycle
    {
        public string Route { get; } = route;

        public bool FailStop { get; set; }

        public FrameworkElement CreateView(Lifetime lifetime)
        {
            order.Add("view");
            return new Border();
        }

        public Task StartRuntimeAsync(
            Lifetime runtimeLifetime,
            RuntimeSession session,
            CancellationToken cancellationToken)
        {
            if (lifecycleFirst) order.Add("lifecycle");
            return Task.CompletedTask;
        }

        public Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken)
        {
            if (FailStop)
            {
                throw new InvalidOperationException("stop boom");
            }
            return Task.CompletedTask;
        }

        public void ForceStopRuntime(RuntimeSession session)
        {
        }
    }

    private sealed class ResidentBridgeModule(string route, List<string> order)
        : IModule, IResidentModule
    {
        public string Route { get; } = route;

        public Lifetime? Attached { get; private set; }

        public FrameworkElement CreateView(Lifetime lifetime)
        {
            order.Add("view");
            return new Border();
        }

        public void AttachApplicationLifetime(Lifetime applicationLifetime)
        {
            Attached = applicationLifetime;
            order.Add("attach");
        }
    }

    private sealed class FailingStartModule(string route)
        : IModule, IRuntimeModuleLifecycle
    {
        public string Route { get; } = route;

        public Lifetime? RuntimeLifetime { get; private set; }

        public FrameworkElement CreateView(Lifetime lifetime) => new Border();

        public Task StartRuntimeAsync(
            Lifetime runtimeLifetime,
            RuntimeSession session,
            CancellationToken cancellationToken)
        {
            RuntimeLifetime = runtimeLifetime;
            throw new InvalidOperationException("start boom");
        }

        public Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ForceStopRuntime(RuntimeSession session)
        {
        }
    }

    private sealed class FlakyStartModule(string route)
        : IModule, IRuntimeModuleLifecycle
    {
        public string Route { get; } = route;

        public List<RuntimeSession> Sessions { get; } = [];

        public FrameworkElement CreateView(Lifetime lifetime) => new Border();

        public Task StartRuntimeAsync(
            Lifetime runtimeLifetime,
            RuntimeSession session,
            CancellationToken cancellationToken)
        {
            Sessions.Add(session);
            if (Sessions.Count == 1)
            {
                throw new InvalidOperationException("first start fails");
            }
            return Task.CompletedTask;
        }

        public Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ForceStopRuntime(RuntimeSession session)
        {
        }
    }

    private sealed class BlockingStartModule(
        string route,
        Task gate) : IModule, IRuntimeModuleLifecycle
    {
        public string Route { get; } = route;

        public FrameworkElement CreateView(Lifetime lifetime) => new Border();

        public async Task StartRuntimeAsync(
            Lifetime runtimeLifetime,
            RuntimeSession session,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
        }

        public Task StopRuntimeAsync(RuntimeSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ForceStopRuntime(RuntimeSession session)
        {
        }
    }
}
