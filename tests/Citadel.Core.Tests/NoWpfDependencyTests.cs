using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Citadel.Core.Tests;

/// <summary>
/// The invariant behind the whole stage, as of D6: Citadel.Contract is the leaf
/// (references no Citadel project) and Citadel.Core references exactly one
/// Citadel project — Citadel.Contract, which owns Lifetime and Log. Verified
/// from the built assemblies' metadata, not assumed from the csproj.
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
        var references = ReadAssemblyReferences(FindAssembly("Citadel.Core"));

        Assert.DoesNotContain(
            references,
            r => Forbidden.Any(f => r.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CitadelCore_ReferencesOnlyCitadelContract()
    {
        var references = ReadAssemblyReferences(FindAssembly("Citadel.Core"))
            .Where(r => r.StartsWith("Citadel", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(["Citadel.Contract"], references);
    }

    [Fact]
    public void CitadelContract_IsTheLeafAndReferencesNoCitadelProject()
    {
        var references = ReadAssemblyReferences(FindAssembly("Citadel.Contract"))
            .Where(r => r.StartsWith("Citadel", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(references);
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

    private static string FindAssembly(string projectName)
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
            root!.FullName, "core", projectName, "bin", config, "net10.0-windows", projectName + ".dll");
        Assert.True(File.Exists(dll), $"expected {dll}");
        return dll;
    }
}
