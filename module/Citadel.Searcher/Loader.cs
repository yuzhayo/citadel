using System.Reflection;
using System.Runtime.Loader;
using Citadel.Core.Modules;

namespace Citadel.Searcher;

/// <summary>
/// Loads one citizen folder's entry assembly into its own collectible
/// <see cref="AssemblyLoadContext"/> and instantiates the declared
/// <see cref="IModule"/>.
///
/// Two decisions here are not obvious and both were measured rather than
/// reasoned about.
///
/// **Contexts are retained, never unloaded on delete.** A deleted entry is
/// hidden immediately, while its context unloads at exit. Unloading on
/// `Unregister` while a
/// destroyed-but-not-collected view still references a type from that context
/// produces a partially-unloaded ALC and a failure that surfaces much later.
///
/// **Assemblies are loaded from a stream, not a path.** `LoadFromAssemblyPath`
/// keeps an OS file handle open for the life of the context, so the deployed
/// folder cannot be deleted while Citadel runs — not by us, and not by the user
/// in Explorer. Reading the bytes first and calling
/// `LoadFromStream` keeps the type usable, keeps the context retained, and
/// leaves the file deletable. Verified for compiled BAML too, which was the
/// real risk: a stream-loaded WPF assembly still resolves its own resources.
///
/// The cost is that `Assembly.Location` is empty for a stream-loaded assembly,
/// so nothing here may derive a path from it. <see cref="AssemblyDependencyResolver"/>
/// is constructed from the deployed entry path *before* loading, for exactly
/// that reason.
///
/// Shared Citadel assemblies must come from the default context or the
/// <see cref="IModule"/> cast breaks. The resolving hook
/// returns null for them, which is what defers to the default context.
/// </summary>
internal sealed class Loader
{
    /// <summary>
    /// Assemblies a citizen must never load a private copy of. A second
    /// `Citadel.Contract` means a second `IModule` type, and the cast fails with
    /// a message that blames the citizen rather than the deployment.
    /// </summary>
    internal static readonly IReadOnlyList<string> SharedAssemblies =
    [
        "Citadel.Core",
        "Citadel.Contract",
        "Citadel.Setting",
        "Citadel.Ui",
        "Citadel.Shell",
    ];

    private readonly Action<string> _log;
    private readonly List<AssemblyLoadContext> _contexts = [];
    private readonly object _gate = new();
    private bool _disposed;

    internal Loader(Action<string> log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>
    /// Loads and instantiates. Returns null with <paramref name="error"/> set on
    /// any failure; the folder stays isolated either way.
    /// </summary>
    internal IModule? Load(
        string folder,
        ModuleManifest manifest,
        out SearchStage stage,
        out string? error)
    {
        stage = SearchStage.Entry;

        if (!File.Exists(manifest.Entry))
        {
            error = $"entry assembly '{Path.GetFileName(manifest.Entry)}' is missing";
            return null;
        }

        AssemblyLoadContext? context = null;
        try
        {
            // Constructed from the real deployed path while it still exists, and
            // before the stream load — a stream-loaded assembly has no Location
            // to recover this from.
            var resolver = new AssemblyDependencyResolver(manifest.Entry);
            context = new AssemblyLoadContext(
                $"citadel-module-{manifest.Route}",
                isCollectible: true);
            context.Resolving += (loadContext, name) => ResolvePrivate(loadContext, resolver, name);

            var assembly = LoadWithoutRetainingHandle(context, manifest.Entry);

            stage = SearchStage.Type;
            var type = assembly.GetType(manifest.Type, throwOnError: false);
            if (type is null)
            {
                error = $"type '{manifest.Type}' is not in {Path.GetFileName(manifest.Entry)}";
                Retain(context);
                return null;
            }
            if (!typeof(IModule).IsAssignableFrom(type))
            {
                error = $"type '{manifest.Type}' does not implement IModule";
                Retain(context);
                return null;
            }

            if (Activator.CreateInstance(type) is not IModule module)
            {
                error = $"type '{manifest.Type}' could not be constructed as an IModule";
                Retain(context);
                return null;
            }

            if (!string.Equals(module.Route, manifest.Route, StringComparison.Ordinal))
            {
                // Otherwise the sidebar entry and the router disagree: the entry
                // says one route, the module answers another, and navigation
                // lands nowhere.
                error =
                    $"type '{manifest.Type}' reports route '{module.Route}' "
                    + $"but {Reader.ManifestFileName} declares '{manifest.Route}'";
                Retain(context);
                return null;
            }

            Retain(context);
            error = null;
            return module;
        }
        catch (Exception exception)
        {
            // A retained context for a folder that failed still costs nothing and
            // must not be unloaded here — the type may already be referenced.
            if (context is not null) Retain(context);
            error = Describe(exception, manifest);
            return null;
        }
    }

    /// <summary>
    /// Unloads every retained context. Called once, at real application exit —
    /// never on folder deletion.
    /// </summary>
    internal void Dispose()
    {
        List<AssemblyLoadContext> contexts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            contexts = [.. _contexts];
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            try
            {
                context.Unload();
            }
            catch (Exception exception)
            {
                _log($"[Loader] '{context.Name}' did not unload: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Reads the file fully, then loads from memory, so no handle survives the
    /// call. The PDB is best-effort: it buys line numbers in citizen stack
    /// traces and its absence is not a failure.
    /// </summary>
    private static Assembly LoadWithoutRetainingHandle(
        AssemblyLoadContext context,
        string path)
    {
        var bytes = File.ReadAllBytes(path);
        var symbols = TryReadSymbols(path);

        using var assemblyStream = new MemoryStream(bytes);
        if (symbols is null) return context.LoadFromStream(assemblyStream);

        using var symbolStream = new MemoryStream(symbols);
        return context.LoadFromStream(assemblyStream, symbolStream);
    }

    private static byte[]? TryReadSymbols(string assemblyPath)
    {
        var pdb = Path.ChangeExtension(assemblyPath, ".pdb");
        try
        {
            return File.Exists(pdb) ? File.ReadAllBytes(pdb) : null;
        }
        catch
        {
            // Diagnostics only. A locked or unreadable PDB must not stop a
            // screen from loading.
            return null;
        }
    }

    private Assembly? ResolvePrivate(
        AssemblyLoadContext context,
        AssemblyDependencyResolver resolver,
        AssemblyName name)
    {
        if (name.Name is null) return null;
        if (SharedAssemblies.Contains(name.Name, StringComparer.OrdinalIgnoreCase))
        {
            // null defers to the default context, which is the whole point: one
            // IModule identity across the app.
            return null;
        }

        var path = resolver.ResolveAssemblyToPath(name);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            return LoadWithoutRetainingHandle(context, path);
        }
        catch (Exception exception)
        {
            _log($"[Loader] private dependency '{name.Name}' failed to load: {exception.Message}");
            return null;
        }
    }

    private void Retain(AssemblyLoadContext context)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                try { context.Unload(); } catch { /* already exiting */ }
                return;
            }
            _contexts.Add(context);
        }
    }

    private static string Describe(Exception exception, ModuleManifest manifest)
    {
        // A citizen shipping its own Citadel.Contract.dll produces exactly this,
        // and the raw message names neither the cause nor the fix.
        if (exception is InvalidCastException or TypeLoadException)
        {
            return $"type '{manifest.Type}' could not be used as an IModule "
                + $"({exception.GetType().Name}: {exception.Message}). "
                + "A screen folder must not ship its own copy of a shared Citadel assembly.";
        }

        var inner = exception.InnerException;
        return inner is null
            ? $"{exception.GetType().Name}: {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message} ({inner.GetType().Name}: {inner.Message})";
    }
}
