using System.IO;
using System.Text;
using Citadel.Core.Modules;
using Citadel.Searcher;

namespace Citadel.Uia;

/// <summary>
/// A real runtime module root in a temp folder, plus real citizen DLLs copied
/// from the built `module/blank` output.
///
/// Deliberately not a mock filesystem: every defect this stage can have — the
/// file handle the loader keeps, whether `AssemblyDependencyResolver` finds a
/// private dependency, whether compiled BAML resolves from a stream-loaded
/// assembly — only reproduces against real files.
/// </summary>
internal sealed class ModuleFolder : IDisposable
{
    private ModuleFolder(string root) => Root = root;

    public string Root { get; }

    public static ModuleFolder Create() => new(Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "citadel-module-" + Guid.NewGuid().ToString("N"))).FullName);

    /// <summary>
    /// Where `module/blank` was deployed by Citizen.targets. Found by walking up
    /// to the solution rather than assumed, so Debug/Release both work.
    /// </summary>
    public static string BlankDeployment => Path.Combine(
        ShellOutput, "module", "blank");

    /// <summary>
    /// The test-only citizen's build output: a real .deps.json and a real private
    /// dependency, which `module/blank` deliberately does not have.
    /// </summary>
    public static string FixtureOutput => Path.Combine(
        SolutionRoot, "tests", "Citadel.Uia.Citizen", "bin", Configuration, "net10.0-windows");

    private static string ShellOutput => Path.Combine(
        SolutionRoot, "core", "Citadel.Shell", "bin", Configuration, "net10.0-windows");

    private static string Configuration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    private static string SolutionRoot
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Citadel.slnx")))
            {
                root = root.Parent;
            }
            Assert.True(root is not null, "Citadel.slnx not found above the test output");
            return root!.FullName;
        }
    }

    /// <summary>A folder holding whatever files the test names.</summary>
    public string Folder(string name, params (string Name, string Content)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        foreach (var (fileName, content) in files)
        {
            File.WriteAllText(Path.Combine(folder, fileName), content, new UTF8Encoding(false));
        }
        return folder;
    }

    /// <summary>
    /// A copy of the built blank citizen — the same bytes the real app discovers.
    /// Route stays `blank` because the DLL hard-codes it; pass
    /// <paramref name="route"/> only to make the manifest disagree on purpose.
    /// </summary>
    public string Citizen(string name, string? route = null, string? layout = null)
    {
        var folder = Copy(name, BlankDeployment,
            $"build module/blank/Module.Blank.csproj first");

        if (route is not null) WriteManifest(folder, route, "Module.Blank.dll", "Module.Blank.BlankModule");
        if (layout is not null)
        {
            File.WriteAllText(Path.Combine(folder, "layout.json"), layout, new UTF8Encoding(false));
        }
        return folder;
    }

    /// <summary>
    /// A copy of the test-only fixture citizen, pointed at one of its types. The
    /// route is derived from the type name, so the manifest and
    /// <c>IModule.Route</c> agree without the test having to lie.
    /// </summary>
    public string Fixture(string name, string type, string? layout = null, bool includeDependency = true)
    {
        var folder = Copy(name, FixtureOutput,
            "build tests/Citadel.Uia.Citizen/Citadel.Uia.Citizen.csproj first");

        if (!includeDependency)
        {
            var dependency = Path.Combine(folder, "Citizen.PrivateDependency.dll");
            if (File.Exists(dependency)) File.Delete(dependency);
        }

        WriteManifest(
            folder,
            type.Split('.').Last().ToLowerInvariant(),
            "Module.Fixture.dll",
            $"Citadel.Uia.Citizen.{type}");
        if (layout is not null)
        {
            File.WriteAllText(Path.Combine(folder, "layout.json"), layout, new UTF8Encoding(false));
        }
        return folder;
    }

    /// <summary>Copies the payload of a built citizen without its manifest.</summary>
    private string Copy(string name, string source, string hint)
    {
        Assert.True(Directory.Exists(source), $"expected a built citizen at {source}; {hint}");

        var folder = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }
        return folder;
    }

    public static void WriteManifest(string folder, string route, string entry, string type) =>
        File.WriteAllText(
            Path.Combine(folder, "module.json"),
            $$"""
            {
              "title": "{{route}}",
              "route": "{{route}}",
              "order": 999,
              "entry": "{{entry}}",
              "type": "{{type}}"
            }
            """,
            new UTF8Encoding(false));

    public void Delete(string name) =>
        Directory.Delete(Path.Combine(Root, name), recursive: true);

    /// <summary>
    /// Deletes while the searcher is running, retrying briefly.
    ///
    /// The distinction this preserves is the whole point of the loader's design.
    /// A *retained* handle — what `LoadFromAssemblyPath` leaves behind — makes the
    /// folder permanently undeletable; no amount of retrying helps. A reconcile
    /// pass that happens to be reading `module.json` at this instant holds a
    /// handle for microseconds. Retrying tells the two apart instead of hiding
    /// either: a real lock still fails here, loudly.
    /// </summary>
    public void DeleteWhileRunning(string name)
    {
        var path = Path.Combine(Root, name);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A retained load context would surface here as a locked DLL — the
            // very thing the loader avoids. Swallowed so a genuine assertion
            // failure is not masked by a cleanup failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Collects everything the gate is told, without a window. `Register` is void by
/// contract, so what a test can observe is the sequence of calls — which is
/// exactly what ownership ordering is about.
/// </summary>
internal sealed class RecordingGate : IModuleGate
{
    private readonly object _gate = new();
    private readonly List<ModuleDescriptor> _registered = [];
    private readonly List<string> _unregistered = [];

    public IReadOnlyList<ModuleDescriptor> Registered
    {
        get { lock (_gate) return [.. _registered]; }
    }

    public IReadOnlyList<string> Unregistered
    {
        get { lock (_gate) return [.. _unregistered]; }
    }

    /// <summary>Routes still live: registered and not later unregistered.</summary>
    public IReadOnlyList<string> LiveRoutes
    {
        get
        {
            lock (_gate)
            {
                var live = new List<string>();
                foreach (var descriptor in _registered)
                {
                    live.Add(descriptor.Route);
                }
                foreach (var route in _unregistered)
                {
                    live.Remove(route);
                }
                return live;
            }
        }
    }

    public void Register(ModuleDescriptor descriptor)
    {
        lock (_gate) _registered.Add(descriptor);
    }

    public void Unregister(string route)
    {
        lock (_gate) _unregistered.Add(route);
    }
}

internal static class Wait
{
    /// <summary>
    /// Polls a condition with a deadline. The searcher is deliberately
    /// asynchronous — debounce, backoff, one pump — so a test either waits or
    /// asserts on a state that has not happened yet.
    /// </summary>
    public static bool Until(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    public static void For(Func<bool> condition, string because, int timeoutMs = 8000) =>
        Assert.True(Until(condition, timeoutMs), because);
}
