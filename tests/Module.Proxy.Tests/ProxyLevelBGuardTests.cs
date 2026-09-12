namespace Module.Proxy.Tests;

using Xunit;

public sealed class ProxyLevelBGuardTests
{
    [Fact]
    public void Parent_RemainsCompositionOnly()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "module", "proxy", "ProxyView.xaml.cs"));
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TcpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncRuntime_IsModuleOwned_NotViewOwned()
    {
        var root = FindRepositoryRoot();
        var module = File.ReadAllText(Path.Combine(root, "module", "proxy", "ProxyModule.cs"));
        var view = File.ReadAllText(Path.Combine(root, "module", "proxy", "ProxyView.xaml.cs"));

        Assert.Contains("private readonly ProxySyncCoordinator _coordinator", module, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProxySyncCoordinator", view, StringComparison.Ordinal);
        Assert.DoesNotContain("_coordinator.Dispose", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedLogic_DoesNotImportFeatureNamespaces()
    {
        var root = FindRepositoryRoot();
        var shared = Path.Combine(root, "module", "proxy", "sharedLogic");
        foreach (var file in Directory.EnumerateFiles(shared, "*.cs"))
        {
            Assert.DoesNotContain("Module.Proxy.Features", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "Citadel.slnx")))
        {
            cursor = cursor.Parent;
        }
        return cursor?.FullName ?? throw new DirectoryNotFoundException("Citadel repository root not found.");
    }
}
