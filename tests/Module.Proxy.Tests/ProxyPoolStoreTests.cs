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

        store.CommitSync([z, a, z]);

        Assert.Equal([a.Canonical, z.Canonical], File.ReadAllLines(store.ActivePath));
        Assert.False(File.Exists(store.ActivePath + ".staging"));
    }

    [Fact]
    public void EmptyCommit_PreservesExistingPool()
    {
        var store = new ProxyPoolStore(_root);
        var existing = Parse("http://old.test:8080");
        store.CommitSync([existing]);

        Assert.Throws<InvalidOperationException>(() => store.CommitSync([]));

        Assert.Equal(existing.Canonical, Assert.Single(File.ReadAllLines(store.ActivePath)));
    }

    [Fact]
    public void SourceResultBannedDuringProbe_DoesNotReplaceItsLastContribution()
    {
        var store = new ProxyPoolStore(_root);
        var existing = Parse("http://old-webshare.test:8080");
        var nowBanned = Parse("http://banned.test:8080");
        store.CommitWebshare([existing]);
        store.Ban([nowBanned]);

        Assert.Throws<InvalidOperationException>(() => store.CommitWebshare([nowBanned]));
        Assert.Equal(existing.Canonical, Assert.Single(store.LoadActive().Endpoints).Canonical);
    }

    [Fact]
    public void Ban_RemovesActiveEntryAndPersistsBan()
    {
        var store = new ProxyPoolStore(_root);
        var keep = Parse("http://keep.test:8080");
        var ban = Parse("http://ban.test:8080");
        store.CommitSync([keep, ban]);

        store.Ban([ban]);

        Assert.Equal(keep.Canonical, Assert.Single(store.LoadActive().Endpoints).Canonical);
        Assert.Equal(ban.Canonical, Assert.Single(store.LoadBanned().Endpoints).Canonical);
        store.CommitWebshare([Parse("http://new.test:8080")]);
        Assert.DoesNotContain(store.LoadActive().Endpoints, endpoint => endpoint.Canonical == ban.Canonical);
    }

    [Fact]
    public void RemoveFromActive_RemovesOnlyTheActiveEntryAndKeepsItUnbanned()
    {
        var store = new ProxyPoolStore(_root);
        var keep = Parse("http://keep.test:8080");
        var remove = Parse("http://remove.test:8081");
        store.CommitSync([keep, remove]);

        store.RemoveFromActive([remove]);

        Assert.Equal(keep.Canonical, Assert.Single(store.LoadActive().Endpoints).Canonical);
        Assert.Empty(store.LoadBanned().Endpoints);
        store.CommitWebshare([Parse("http://new.test:8080")]);
        Assert.DoesNotContain(store.LoadActive().Endpoints, endpoint => endpoint.Canonical == remove.Canonical);
    }

    [Fact]
    public void Commit_StoresOnlyActiveWebshareOriginMetadata()
    {
        var store = new ProxyPoolStore(_root);
        var webshare = Parse("http://user:pass@p.webshare.io:10000");
        var generic = Parse("http://generic.test:8080");

        store.CommitWebshare(
            [webshare, generic],
            origins: [(webshare, new ProxyPoolOrigin("webshare", "ws-account-a"))]);

        var origins = store.LoadOrigins();
        var entry = Assert.Single(origins.Entries);
        Assert.Equal("ws-account-a", entry.Value.AccountId);
        Assert.DoesNotContain("user:pass", File.ReadAllText(store.OriginsPath), StringComparison.Ordinal);

        store.RemoveFromActive([webshare]);
        Assert.Empty(store.LoadOrigins().Entries);
    }

    [Fact]
    public void FirstSync_PreservesLegacyWebsharePoolAndOrigin()
    {
        var store = new ProxyPoolStore(_root);
        var webshare = Parse("http://user:pass@legacy.test:8080");
        var synced = Parse("socks5://synced.test:1080");
        AtomicTextFile.Write(store.ActivePath, [webshare.Canonical]);
        AtomicTextFile.WriteAllText(store.OriginsPath, ProxyPoolOriginContract.Serialize(
            [(webshare, new ProxyPoolOrigin("webshare", "ws-legacy"))], [webshare]));

        store.CommitSync([synced]);

        Assert.Equal(2, store.LoadActive().Endpoints.Length);
        Assert.Equal("ws-legacy", Assert.Single(store.LoadOrigins().Entries).Value.AccountId);
        Assert.Equal(webshare.Canonical, Assert.Single(ProxyPoolContract.ReadSnapshot(store.WebsharePath).Endpoints).Canonical);
        Assert.Equal(synced.Canonical, Assert.Single(ProxyPoolContract.ReadSnapshot(store.SyncPath).Endpoints).Canonical);
    }

    [Fact]
    public void FirstWebshareImport_PreservesLegacySyncPool()
    {
        var store = new ProxyPoolStore(_root);
        var synced = Parse("socks5://legacy-sync.test:1080");
        var webshare = Parse("http://new-webshare.test:8080");
        AtomicTextFile.Write(store.ActivePath, [synced.Canonical]);

        store.CommitWebshare([webshare], origins: [(webshare, new ProxyPoolOrigin("webshare", "ws-new"))]);

        Assert.Equal(new[] { synced.Canonical, webshare.Canonical }.Order(StringComparer.Ordinal),
            store.LoadActive().Endpoints.Select(endpoint => endpoint.Canonical).Order(StringComparer.Ordinal));
        Assert.Equal("ws-new", Assert.Single(store.LoadOrigins().Entries).Value.AccountId);
    }

    [Fact]
    public void EachSourceRefresh_ReplacesOnlyItsOwnContribution()
    {
        var store = new ProxyPoolStore(_root);
        var syncOld = Parse("http://sync-old.test:8080");
        var syncNew = Parse("http://sync-new.test:8080");
        var webOld = Parse("http://web-old.test:8080");
        var webNew = Parse("http://web-new.test:8080");
        store.CommitSync([syncOld]);
        store.CommitWebshare([webOld], origins: [(webOld, new ProxyPoolOrigin("webshare", "ws-old"))]);

        store.CommitSync([syncNew]);
        Assert.Equal([syncNew.Canonical, webOld.Canonical],
            store.LoadActive().Endpoints.Select(endpoint => endpoint.Canonical).Order(StringComparer.Ordinal));
        Assert.Equal("ws-old", Assert.Single(store.LoadOrigins().Entries).Value.AccountId);

        var reopened = new ProxyPoolStore(_root);
        reopened.CommitWebshare([webNew], origins: [(webNew, new ProxyPoolOrigin("webshare", "ws-new"))]);
        Assert.Equal([syncNew.Canonical, webNew.Canonical],
            reopened.LoadActive().Endpoints.Select(endpoint => endpoint.Canonical).Order(StringComparer.Ordinal));
        Assert.Equal("ws-new", Assert.Single(reopened.LoadOrigins().Entries).Value.AccountId);
    }

    [Fact]
    public void CombinedPool_DeduplicatesOverlapAndRetainsOtherSourceHealth()
    {
        var store = new ProxyPoolStore(_root);
        var synced = Parse("http://synced.test:8080");
        var shared = Parse("http://shared.test:8080");
        var webshare = Parse("http://webshare.test:8080");
        var checkedAt = DateTimeOffset.UtcNow;
        var syncHealth = new ProxyHealthRecord(
            ProxyPoolHealthContract.EndpointKey(synced), ProxyHealthState.Healthy, 10, checkedAt, null);
        var webHealth = new ProxyHealthRecord(
            ProxyPoolHealthContract.EndpointKey(webshare), ProxyHealthState.Healthy, 20, checkedAt, null);

        store.CommitSync([synced, shared], [syncHealth]);
        store.CommitWebshare([shared, webshare], [webHealth]);

        Assert.Equal(3, store.LoadActive().Endpoints.Length);
        Assert.Contains(ProxyPoolHealthContract.EndpointKey(synced), store.LoadHealth().Entries.Keys);
        Assert.Contains(ProxyPoolHealthContract.EndpointKey(webshare), store.LoadHealth().Entries.Keys);
    }

    [Fact]
    public void HealthRefreshStartedBeforeSourceCommit_DoesNotEraseNewSourceHealth()
    {
        var store = new ProxyPoolStore(_root);
        var synced = Parse("http://synced.test:8080");
        var webshare = Parse("http://webshare.test:8080");
        store.CommitSync([synced]);
        var checkedAt = DateTimeOffset.UtcNow;
        var syncHealth = new ProxyHealthRecord(
            ProxyPoolHealthContract.EndpointKey(synced), ProxyHealthState.Healthy, 10, checkedAt, null);
        var webHealth = new ProxyHealthRecord(
            ProxyPoolHealthContract.EndpointKey(webshare), ProxyHealthState.Healthy, 20, checkedAt, null);
        store.CommitWebshare([webshare], [webHealth]);

        store.SaveHealth([syncHealth], [synced]);

        Assert.Equal(2, store.LoadHealth().Entries.Count);
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
