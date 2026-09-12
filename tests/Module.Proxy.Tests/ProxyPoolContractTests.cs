using CitadelBridge;
using Xunit;

namespace Module.Proxy.Tests;

public sealed class ProxyPoolContractTests
{
    [Fact]
    public void ParseLines_NormalizesDefaultsDeduplicatesAndMasksCredentials()
    {
        var result = ProxyPoolContract.ParseLines(
        [
            "Example.COM:8080",
            "http://example.com:8080",
            "socks5://user:secret@Host.test:1080",
            "ftp://bad.test:21",
            "not-a-proxy",
        ], "http");

        Assert.Equal(2, result.Endpoints.Length);
        Assert.Equal(2, result.SkippedLines);
        var authenticated = Assert.Single(result.Endpoints, item => item.HasAuthentication);
        Assert.Equal("socks5://***@host.test:1080", authenticated.Masked);
        Assert.DoesNotContain("secret", authenticated.Masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("https://host.test:443")]
    [InlineData("socks4://host.test:1080")]
    [InlineData("socks5://host.test:1080")]
    public void TryParse_AcceptsSupportedSchemes(string value) =>
        Assert.True(ProxyPoolContract.TryParse(value, null, out _));

    [Theory]
    [InlineData("http://host.test")]
    [InlineData("ftp://host.test:21")]
    [InlineData("http://host.test:70000")]
    [InlineData("http://host.test:80/path")]
    public void TryParse_RejectsInvalidShape(string value) =>
        Assert.False(ProxyPoolContract.TryParse(value, null, out _));
}
