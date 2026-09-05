using System.IO;
using Module.Mangareader.Features.Downloader;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Destination containment: a browser or native write can never escape the
/// Downloader's own staging root. This is the C# half of the two-sided check the
/// Python plugin repeats.
/// </summary>
public sealed class StagingContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Downloader.Containment.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly DownloaderPyHostClient _client;

    public StagingContainmentTests()
    {
        Directory.CreateDirectory(_root);
        _client = new DownloaderPyHostClient(_root);
    }

    [Fact]
    public void StagingRootIsCanonicalAndCreated()
    {
        Assert.True(Directory.Exists(_client.StagingRoot));
        Assert.Equal(Path.GetFullPath(_root), _client.StagingRoot);
    }

    [Fact]
    public void PathsInsideStagingResolve()
    {
        var resolved = _client.ResolveContained(
            Path.Combine("jobs", "abc123", "pages", "00001-page.png"));

        Assert.StartsWith(
            _client.StagingRoot + Path.DirectorySeparatorChar,
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../../escape.png")]
    [InlineData("../escape.png")]
    [InlineData("jobs/../../escape.png")]
    public void RelativeEscapesAreRefused(string relativePath)
    {
        Assert.Throws<InvalidOperationException>(
            () => _client.ResolveContained(relativePath));
    }

    [Fact]
    public void AbsolutePathsOutsideStagingAreRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "citadel-escape-" + Guid.NewGuid().ToString("N") + ".png");

        Assert.Throws<InvalidOperationException>(() => _client.ResolveContained(outside));
    }

    [Fact]
    public void ASiblingFolderSharingTheRootPrefixIsRefused()
    {
        // "<staging>-evil" starts with the staging string but is not inside it,
        // which is exactly what a naive StartsWith check would let through.
        var sibling = _client.StagingRoot.TrimEnd(Path.DirectorySeparatorChar) + "-evil";

        Assert.Throws<InvalidOperationException>(
            () => _client.ResolveContained(Path.Combine(sibling, "page.png")));
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
