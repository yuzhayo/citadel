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

    [Fact]
    public async Task StopAfterFirstVerifiedProxy_AppendsPartialSyncWithoutDroppingExistingPool()
    {
        var source = new ProxySource("one", new Uri("https://source.test/list"), "http");
        var probe = new FirstHealthyThenBlockingProbe();
        var service = new ProxySyncService(
            new StaticFetcher("new.test:80\nwaiting.test:81"), probe, [source]);
        var store = new ProxyPoolStore(_root);
        store.CommitSync([Parse("http://old-sync.test:80")]);
        store.CommitWebshare([Parse("http://old-webshare.test:80")]);
        var settings = new ProxySettingsStore(Path.Combine(_root, "settings.json"));
        settings.Save(new ProxySettings(ParallelTcpChecks: 1));
        using var coordinator = new ProxySyncCoordinator(service, store, settings);

        var running = coordinator.StartAsync();
        await probe.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Stop();
        await running;

        Assert.Contains("saved 1", coordinator.CurrentState.Status, StringComparison.Ordinal);
        Assert.Equal(3, store.LoadActive().Endpoints.Length);
        Assert.Contains(store.LoadActive().Endpoints, item => item.Host == "new.test");
        Assert.DoesNotContain(store.LoadActive().Endpoints, item => item.Host == "waiting.test");
        Assert.Contains(store.LoadHealth().Entries.Values, item => item.State == ProxyHealthState.Healthy);
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

    private sealed class FirstHealthyThenBlockingProbe : IProxyReachabilityProbe
    {
        private int _calls;
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProxyProbeResult> ProbeAsync(
            ProxyEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                SecondStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new ProxyProbeResult(true, 10, "OK");
        }
    }

    private static ProxyEndpoint Parse(string value)
    {
        Assert.True(ProxyPoolContract.TryParse(value, null, out var endpoint));
        return endpoint;
    }
}
