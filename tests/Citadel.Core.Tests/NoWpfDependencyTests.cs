using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Citadel.Core.Tests;

/// <summary>
/// The invariant behind the whole stage: Citadel.Core references
/// nothing. Verified from the built assembly's metadata, not assumed
/// from the csproj.
/// </summary>
public class NoWpfDependencyTests
{
    private static readonly string[] Forbidden =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "PresentationUI",
        "System.Windows",
    ];

    [Fact]
    public void CitadelCore_ReferencesNoWpf()
    {
        var references = ReadAssemblyReferences(FindCoreAssembly());

        Assert.DoesNotContain(
            references,
            r => Forbidden.Any(f => r.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CitadelCore_ReferencesNoOtherCitadelProject()
    {
        var references = ReadAssemblyReferences(FindCoreAssembly());

        Assert.DoesNotContain(
            references,
            r => r.StartsWith("Citadel", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ReadAssemblyReferences(string dllPath)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences
            .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .ToList();
    }

    private static string FindCoreAssembly()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = baseDir.Parent!.Name; // Debug/Release sits above the TFM folder

        var root = baseDir;
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "Citadel.slnx")))
        {
            root = root.Parent;
        }
        Assert.True(root is not null, "Citadel.slnx not found above the test output");

        var dll = System.IO.Path.Combine(
            root!.FullName, "core", "Citadel.Core", "bin", config, "net10.0-windows", "Citadel.Core.dll");
        Assert.True(File.Exists(dll), $"expected {dll}");
        return dll;
    }
}
