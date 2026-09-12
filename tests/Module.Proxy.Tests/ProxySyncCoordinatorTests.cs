using System.IO;
using CitadelBridge;
using Module.Proxy.Features.Sync;
using Module.Proxy.SharedLogic;
using Xunit;

namespace Module.Proxy.Tests;

public sealed class ProxySyncCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "proxy-coordinator-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartStopStart_ReleasesOldJobBeforeStartIsEnabled()
    {
        var source = new ProxySource("one", new Uri("https://source.test/list"), "http");
        var probe = new FirstRunBlockingProbe();
        var service = new ProxySyncService(
            new StaticFetcher("one.test:80"),
            probe,
            [source]);
        var store = new ProxyPoolStore(_root);
        var settings = new ProxySettingsStore(Path.Combine(_root, "settings.json"));
        settings.Save(new ProxySettings(ParallelTcpChecks: 1));
        using var coordinator = new ProxySyncCoordinator(service, store, settings);
        ProxySyncState? last = null;
        coordinator.StateChanged += (_, state) => last = state;

        var first = coordinator.StartAsync();
        await probe.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Stop();
        await first;

        Assert.False(coordinator.IsRunning);
        Assert.False(last?.IsRunning);

        await coordinator.StartAsync();

        Assert.False(coordinator.IsRunning);
        Assert.False(last?.IsRunning);
        Assert.Single(store.LoadActive().Endpoints);
    }

    [Fact]
    public async Task DetachedObserver_DoesNotOwnJob_AndCurrentStateRemainsAvailable()
    {
        var source = new ProxySource("one", new Uri("https://source.test/list"), "http");
        var probe = new FirstRunBlockingProbe();
        var service = new ProxySyncService(
            new StaticFetcher("one.test:80"),
            probe,
            [source]);
        var store = new ProxyPoolStore(_root);
        var settings = new ProxySettingsStore(Path.Combine(_root, "settings.json"));
        settings.Save(new ProxySettings(ParallelTcpChecks: 1));
        using var coordinator = new ProxySyncCoordinator(service, store, settings);
        EventHandler<ProxySyncState> observer = (_, _) => { };
        coordinator.StateChanged += observer;

        var running = coordinator.StartAsync();
        await probe.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.StateChanged -= observer;

        Assert.True(coordinator.IsRunning);
        Assert.True(coordinator.CurrentState.IsRunning);

        coordinator.Stop();
        await running;

        Assert.False(coordinator.CurrentState.IsRunning);
        Assert.Contains("Cancelled", coordinator.CurrentState.Status, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class StaticFetcher(string value) : IProxySourceFetcher
    {
        public Task<string> FetchAsync(
            ProxySource source,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(value);
    }

    private sealed class FirstRunBlockingProbe : IProxyReachabilityProbe
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProxyProbeResult> ProbeAsync(
            ProxyEndpoint endpoint,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new ProxyProbeResult(true, 10, "OK");
        }
    }
}
