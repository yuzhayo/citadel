using CitadelBridge;
using Module.Proxy.SharedLogic;
using Xunit;

namespace Module.Proxy.Tests;

public sealed class ProxyPoolStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "citadel-proxy-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Commit_AtomicallyPublishesSortedPool()
    {
        var store = new ProxyPoolStore(_root);
        var z = Parse("socks5://z.test:1080");
        var a = Parse("http://a.test:8080");

        store.Commit([z, a, z]);

        Assert.Equal([a.Canonical, z.Canonical], File.ReadAllLines(store.ActivePath));
        Assert.False(File.Exists(store.ActivePath + ".staging"));
    }

    [Fact]
    public void EmptyCommit_PreservesExistingPool()
    {
        var store = new ProxyPoolStore(_root);
        var existing = Parse("http://old.test:8080");
        store.Commit([existing]);

        Assert.Throws<InvalidOperationException>(() => store.Commit([]));

        Assert.Equal(existing.Canonical, Assert.Single(File.ReadAllLines(store.ActivePath)));
    }

    [Fact]
    public void Ban_RemovesActiveEntryAndPersistsBan()
    {
        var store = new ProxyPoolStore(_root);
        var keep = Parse("http://keep.test:8080");
        var ban = Parse("http://ban.test:8080");
        store.Commit([keep, ban]);

        store.Ban([ban]);

        Assert.Equal(keep.Canonical, Assert.Single(store.LoadActive().Endpoints).Canonical);
        Assert.Equal(ban.Canonical, Assert.Single(store.LoadBanned().Endpoints).Canonical);
    }

    [Fact]
    public void RemoveFromActive_RemovesOnlyTheActiveEntryAndKeepsItUnbanned()
    {
        var store = new ProxyPoolStore(_root);
        var keep = Parse("http://keep.test:8080");
        var remove = Parse("http://remove.test:8081");
        store.Commit([keep, remove]);

        store.RemoveFromActive([remove]);

        Assert.Equal(keep.Canonical, Assert.Single(store.LoadActive().Endpoints).Canonical);
        Assert.Empty(store.LoadBanned().Endpoints);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ProxyEndpoint Parse(string value)
    {
        Assert.True(ProxyPoolContract.TryParse(value, null, out var endpoint));
        return endpoint;
    }
}
