using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Windows;
using Citadel.Searcher;

namespace Citadel.Uia;

/// <summary>
/// The loader's two measured decisions: a retained collectible context that does
/// not lock the folder, and shared assemblies resolving from the default context.
///
/// Both were reproduced with throwaway probes before being written down.
/// `LoadFromAssemblyPath` holds an OS handle for the life of the context, which
/// makes the stage's own delete gate impossible; `LoadFromStream` does not, and
/// compiled BAML still resolves.
/// </summary>
[Collection("Shell power saving serial")]
public class SearcherLoaderTests
{
    private static readonly string[] Reserved = ["settings"];

    /// <summary>
    /// The finding that shaped `Loader`. Left as a test because the fix is one
    /// call away from being undone by someone reaching for the obvious API.
    /// </summary>
    [Fact]
    public void LoadFromAssemblyPath_WouldLockTheFolder_WhichIsWhyStreamsAreUsed()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Fixture("locked", "Shared");
        var entry = Path.Combine(folder, "Module.Fixture.dll");

        var context = new System.Runtime.Loader.AssemblyLoadContext("probe", isCollectible: true);
        try
        {
            context.LoadFromAssemblyPath(entry);
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(entry));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// The gate this stage actually has to pass: the folder is deletable while
    /// the app runs, and the loaded type keeps working afterwards.
    ///
    /// Uses the fixture citizen rather than blank, deliberately. WPF caches
    /// compiled-BAML lookups by simple assembly name process-wide, so only one
    /// test may be the BAML proof — see
    /// <see cref="AStreamLoadedCitizenStillResolvesItsCompiledBaml"/>. The
    /// fixture has no XAML, so this test measures file handles and nothing else.
    /// </summary>
    [Fact]
    public void ARetainedContextDoesNotLockTheDeployedFolder()
    {
        Sta.Run(() =>
        {
            using var root = ModuleFolder.Create();
            var gate = new RecordingGate();
            using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

            root.Fixture("held", "Shared");
            searcher.Start();
            Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");

            var module = gate.Registered.Single().Instance;

            // The whole point: still loaded, folder gone.
            root.DeleteWhileRunning("held");
            Assert.False(Directory.Exists(Path.Combine(root.Root, "held")));

            var view = module.CreateView(new Citadel.Core.Rpl.Lifetime());
            Assert.IsAssignableFrom<FrameworkElement>(view);
        });
    }

    /// <summary>
    /// A stream-loaded WPF assembly still finds its own compiled BAML. This was
    /// the real risk in choosing streams and is why the decision was probed
    /// rather than assumed.
    ///
    /// The only test that loads `Module.Blank` and renders it. WPF's
    /// compiled-BAML cache is keyed by simple assembly name process-wide, so a
    /// second test loading a second copy of `Module.Blank` in another context
    /// makes whichever loses unable to find its resources — a real deployment
    /// hazard recorded in `module/README.md`, and the reason this proof stands
    /// alone.
    /// </summary>
    [Fact]
    public void AStreamLoadedCitizenStillResolvesItsCompiledBaml()
    {
        Sta.Run(() =>
        {
            using var root = ModuleFolder.Create();
            var gate = new RecordingGate();
            using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

            root.Citizen("blank");
            searcher.Start();
            Wait.For(() => gate.LiveRoutes.Contains("blank"), "blank should register");

            var view = gate.Registered.Single().Instance
                .CreateView(new Citadel.Core.Rpl.Lifetime());

            // InitializeComponent is what loads the BAML; a named element proves
            // it ran rather than silently producing an empty control.
            Assert.NotNull(view.FindName("Message"));
        });
    }

    /// <summary>
    /// Stream-loaded assemblies have an empty Location, so nothing in the loader
    /// may derive a path from it. Pinned because a future edit reaching for
    /// Assembly.Location would compile and then fail at runtime.
    /// </summary>
    [Fact]
    public void AStreamLoadedAssemblyHasNoLocationToDerivePathsFrom()
    {
        Sta.Run(() =>
        {
            using var root = ModuleFolder.Create();
            var gate = new RecordingGate();
            using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

            root.Fixture("streamed", "Shared");
            searcher.Start();
            Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");

            var assembly = gate.Registered.Single().Instance.GetType().Assembly;
            Assert.Equal(string.Empty, assembly.Location);
        });
    }

    /// <summary>
    /// One IModule identity across the app. A citizen resolving its own
    /// Citadel.Contract would break the cast, so the type must be reference-equal
    /// to the one the test project loaded from the default context.
    /// </summary>
    [Fact]
    public void SharedContractTypeIdentityIsPreserved()
    {
        Sta.Run(() =>
        {
            using var root = ModuleFolder.Create();
            var gate = new RecordingGate();
            using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

            root.Fixture("identity", "Shared");
            searcher.Start();
            Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");

            var citizenContract = gate.Registered.Single().Instance.GetType()
                .GetInterface(typeof(Citadel.Core.Modules.IModule).FullName!)!;
            Assert.Same(typeof(Citadel.Core.Modules.IModule), citizenContract);
        });
    }

    /// <summary>
    /// The searcher references Contract and nothing else among Citadel
    /// assemblies. Read from built metadata rather than the csproj, because the
    /// csproj is what a well-meaning edit changes.
    /// </summary>
    [Fact]
    public void SearcherReferencesContractOnlyAmongCitadelAssemblies()
    {
        var searcher = typeof(Watcher).Assembly.Location;
        Assert.True(File.Exists(searcher), $"expected {searcher}");

        using var stream = File.OpenRead(searcher);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var citadelReferences = reader.AssemblyReferences
            .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .Where(name => name.StartsWith("Citadel", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Citadel.Contract"], citadelReferences);
    }

    [Fact]
    public void AMissingEntryAssemblyIsAFolderFailure()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Folder("ghost", ("module.json", """
            {
              "title": "Ghost",
              "route": "ghost",
              "entry": "Module.Ghost.dll",
              "type": "Module.Ghost.GhostModule"
            }
            """));
        searcher.Start();

        Wait.For(
            () => searcher.Failures().Any(f =>
                f.Folder == "ghost" && f.Stage == SearchStage.Entry),
            "a missing entry assembly should fail at the Entry stage");
        Assert.Empty(gate.Registered);
    }

    [Fact]
    public void ATypeAbsentFromTheAssemblyIsAFolderFailure()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        var folder = root.Fixture("wrongtype", "Shared");
        ModuleFolder.WriteManifest(
            folder, "wrongtype", "Module.Fixture.dll", "Citadel.Uia.Citizen.NoSuchModule");
        searcher.Start();

        Wait.For(
            () => searcher.Failures().Any(f =>
                f.Folder == "wrongtype"
                && f.Stage == SearchStage.Type
                && f.Message.Contains("is not in")),
            "a type absent from the DLL should fail at the Type stage");
        Assert.Empty(gate.Registered);
    }

    /// <summary>
    /// A type that exists but is not an IModule. Uses this test assembly's own
    /// type so the case does not need a second fixture project.
    /// </summary>
    [Fact]
    public void ATypeThatIsNotAModuleIsAFolderFailure()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        var folder = root.Folder("notamodule", ("module.json", $$"""
            {
              "title": "Not a module",
              "route": "notamodule",
              "entry": "{{Path.GetFileName(typeof(SearcherLoaderTests).Assembly.Location)}}",
              "type": "Citadel.Uia.SearcherLoaderTests"
            }
            """));
        File.Copy(
            typeof(SearcherLoaderTests).Assembly.Location,
            Path.Combine(folder, Path.GetFileName(typeof(SearcherLoaderTests).Assembly.Location)));
        searcher.Start();

        Wait.For(
            () => searcher.Failures().Any(f =>
                f.Folder == "notamodule" && f.Stage == SearchStage.Type),
            "a type that is not an IModule should fail at the Type stage");
        Assert.Empty(gate.Registered);
    }

    /// <summary>
    /// The sidebar entry and the router must agree. A module answering a
    /// different route than its manifest declares is refused.
    /// </summary>
    [Fact]
    public void AModuleWhoseRouteDisagreesWithItsManifestIsRefused()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        // The type answers Route => "shared"; the manifest claims otherwise.
        var folder = root.Fixture("liar", "Shared");
        ModuleFolder.WriteManifest(
            folder, "somethingelse", "Module.Fixture.dll", "Citadel.Uia.Citizen.Shared");
        searcher.Start();

        Wait.For(
            () => searcher.Failures().Any(f =>
                f.Folder == "liar" && f.Message.Contains("reports route")),
            "a module contradicting its manifest should be refused");
        Assert.Empty(gate.Registered);
    }
}
