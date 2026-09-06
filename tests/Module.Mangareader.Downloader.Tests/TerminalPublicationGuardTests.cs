using System.Reflection;
using Module.Mangareader.Features.Downloader.Queue;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The terminal publication guard. A completed chapter download ends at its own
/// durable bookkeeping and staging cleanup:
///
/// <code>download -&gt; validate -&gt; compile CBZ -&gt; atomic publish
/// -&gt; queue/source-index bookkeeping -&gt; staging cleanup -&gt; Completed</code>
///
/// Scope of these gates, stated plainly: they reflect over the declared shape of
/// the completion owners — fields, properties, events, constructors, method
/// parameters and return types, including generic arguments. They therefore catch a
/// forbidden dependency that is visible in a signature, and they do not read method
/// bodies, so a forbidden call written inside a method body would still pass.
///
/// The behaviour half of the terminal flow is the existing CBZ publication test.
/// The completion-chain ban itself — a successful disposable download while Library
/// is open must not crash, scan, refresh, build a cover, list, or check for
/// updates — is a live manual gate recorded in the plan and is not proven here.
/// </summary>
public sealed class TerminalPublicationGuardTests
{
    /// <summary>
    /// Features whose work must never be reached from a completed chapter. Each
    /// runs only from its own approved trigger: Library from a manual Scan, Cover
    /// Builder from its own screen, Lister and Auto Cover from the Catalog detail,
    /// and the Update Checker from a manual Check Updates.
    /// </summary>
    private static readonly string[] ForbiddenReferences =
    [
        "LibraryScanner",
        "CoverBuilder",
        "CoverSourceLoader",
        "ListerFeature",
        "AutoCover",
        "CoverCandidate",
        "UpdateChecker",
        "UpdateMatcher",
        "SourceBindingStore",
        "LibraryView",
        "MangaReaderView",
        "HistoryView",
    ];

    /// <summary>The types that actually carry a job to Completed.</summary>
    private static readonly Type[] CompletionOwners =
    [
        typeof(DownloadQueueFeature),
        typeof(ChapterDownloadPipeline),
        typeof(CbzChapterPublisher),
    ];

    [Fact]
    public void TheQueueOwnsExactlyOneChangeSignal()
    {
        var events = typeof(DownloadQueueFeature)
            .GetEvents(DeclaredOnly)
            .Select(eventInfo => eventInfo.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // One signal, and it is the one named in the route surface. A second
        // completion event is exactly how a post-download chain would be rewired,
        // and it would render both screens twice for one job.
        Assert.Equal(["QueueSummaryChanged"], events);
    }

    [Fact]
    public void NoCompletionOwnerReferencesAnyForbiddenFeature()
    {
        foreach (var owner in CompletionOwners)
        {
            var referenced = ReferencedTypeNames(owner);
            foreach (var forbidden in ForbiddenReferences)
            {
                Assert.DoesNotContain(
                    referenced,
                    name => name.Contains(forbidden, StringComparison.Ordinal));
            }
        }
    }

    private const BindingFlags DeclaredOnly =
        BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Every type this owner names in its own shape: fields, properties, events,
    /// constructors, method parameters and return types, including generic
    /// arguments so a delegate cannot smuggle a forbidden dependency through.
    /// </summary>
    private static IReadOnlyList<string> ReferencedTypeNames(Type owner)
    {
        var names = new List<string>();

        foreach (var field in owner.GetFields(DeclaredOnly)) Add(names, field.FieldType);
        foreach (var property in owner.GetProperties(DeclaredOnly)) Add(names, property.PropertyType);
        foreach (var eventInfo in owner.GetEvents(DeclaredOnly)) Add(names, eventInfo.EventHandlerType!);
        foreach (var constructor in owner.GetConstructors(DeclaredOnly))
        {
            foreach (var parameter in constructor.GetParameters()) Add(names, parameter.ParameterType);
        }

        foreach (var method in owner.GetMethods(DeclaredOnly))
        {
            Add(names, method.ReturnType);
            foreach (var parameter in method.GetParameters()) Add(names, parameter.ParameterType);
        }

        return names;
    }

    private static void Add(List<string> names, Type type)
    {
        names.Add(type.Name);
        foreach (var argument in type.GetGenericArguments())
        {
            Add(names, argument);
        }
    }
}
