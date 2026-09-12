using CitadelBridge;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

public sealed class ProxyLeaseRegistryTests
{
    [Fact]
    public async Task BusyEndpointWaitsUntilBodyOwnerReleasesIt()
    {
        var registry = new ProxyLeaseRegistry();
        var candidates = ProxyPoolContract.ParseLines(["http://one.test:80"]).Endpoints;
        using var first = await registry.ReserveAsync("chapter-a", candidates, CancellationToken.None);
        var pending = registry.ReserveAsync("chapter-b", candidates, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Single(registry.Snapshot());
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("chapter-b", Assert.Single(registry.Snapshot()).Owner);
    }

    [Fact]
    public async Task CancelledWaitDoesNotOwnOrLeakAProxy()
    {
        var registry = new ProxyLeaseRegistry();
        var candidates = ProxyPoolContract.ParseLines(["http://one.test:80"]).Endpoints;
        using var first = await registry.ReserveAsync("browser", candidates, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = registry.ReserveAsync("page", candidates, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        Assert.Empty(registry.Snapshot());
    }
}
