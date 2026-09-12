using CitadelBridge;
using Xunit;

namespace Module.Proxy.Tests;

public sealed class ProxyPoolHealthContractTests
{
    [Fact]
    public void EndpointKey_DiffersForDifferentCredentialsAndNeverExposesThem()
    {
        var first = Parse("http://user-one:secret-one@proxy.test:80");
        var second = Parse("http://user-two:secret-two@proxy.test:80");

        var firstKey = ProxyPoolHealthContract.EndpointKey(first);
        var secondKey = ProxyPoolHealthContract.EndpointKey(second);

        Assert.NotEqual(firstKey, secondKey);
        Assert.DoesNotContain("secret", firstKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_UsesNewestRecordAndDoesNotContainCanonicalEndpoint()
    {
        var endpoint = Parse("http://name:password@proxy.test:80");
        var key = ProxyPoolHealthContract.EndpointKey(endpoint);
        var json = ProxyPoolHealthContract.Serialize(
        [
            new ProxyHealthRecord(key, ProxyHealthState.Unreachable, null, DateTimeOffset.UnixEpoch, "Timeout"),
            new ProxyHealthRecord(key, ProxyHealthState.Healthy, 41, DateTimeOffset.UnixEpoch.AddMinutes(1), "OK"),
        ]);

        Assert.Contains("Healthy", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint.Canonical, json, StringComparison.Ordinal);
    }

    private static ProxyEndpoint Parse(string value)
    {
        Assert.True(ProxyPoolContract.TryParse(value, null, out var endpoint));
        return endpoint;
    }
}
