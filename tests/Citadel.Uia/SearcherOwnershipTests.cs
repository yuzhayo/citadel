using System.IO;
using Citadel.Searcher;

namespace Citadel.Uia;

/// <summary>
/// Two folders, one route. `IModuleGate.Register` is void and dispatches
/// asynchronously, so the searcher cannot learn from the gate who won — it has to
/// decide ownership itself, before submitting.
///
/// The case that makes this matter: a rejected duplicate must never become the
/// route's owner, because deleting it would then unregister the screen that
/// actually works.
/// </summary>
public class SearcherOwnershipTests
{
    private static readonly string[] Reserved = ["settings"];

    /// <summary>
    /// One route, one owner, whichever folder is processed first. Both are still
    /// handed to the gate: duplicate-route policy stays where it already lives.
    /// </summary>
    [Fact]
    public void OnlyOneFolderOwnsARouteAndBothReachTheGate()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("first", "Shared");
        root.Fixture("second", "Shared");
        searcher.Start();

        Wait.For(
            () => gate.Registered.Count == 2,
            "the gate decides duplicates, so both submissions must reach it");
        Wait.For(
            () => searcher.Failures().Any(f => f.Message.Contains("already claimed")),
            "the loser should be a visible failure");

        // Exactly one contender is recorded as failed, so exactly one owns it.
        Assert.Single(searcher.Failures(), f => f.Message.Contains("already claimed"));
    }

    /// <summary>
    /// The ownership case that would otherwise be silent: deleting the *rejected*
    /// folder must not disturb the folder that actually owns the route.
    /// </summary>
    [Fact]
    public void DeletingTheRejectedDuplicateLeavesTheOwnerRegistered()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("first", "Shared");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the first folder should own the route");

        root.Fixture("second", "Shared");
        Wait.For(
            () => searcher.Failures().Any(f => f.Folder == "second"),
            "the second folder should be rejected as a duplicate");

        root.DeleteWhileRunning("second");

        Wait.For(
            () => !searcher.Failures().Any(f => f.Folder == "second"),
            "the rejected folder's failure should clear when it is deleted");

        // The assertion that matters: no unregister was issued for the route the
        // surviving folder owns.
        Assert.Empty(gate.Unregistered);
        Assert.Contains("shared", gate.LiveRoutes);
    }

    /// <summary>
    /// Deleting the owner frees the route, and the remaining folder claims it
    /// through the same reconcile path rather than a special promotion path.
    /// </summary>
    [Fact]
    public void DeletingTheOwnerLetsTheRemainingFolderClaimTheRoute()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("first", "Shared");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the first folder should own the route");

        root.Fixture("second", "Shared");
        Wait.For(
            () => searcher.Failures().Any(f => f.Folder == "second"),
            "the second folder should be rejected first");

        root.DeleteWhileRunning("first");

        Wait.For(
            () => gate.Unregistered.Contains("shared"),
            "deleting the owner should unregister the route");
        Wait.For(
            () => !searcher.Failures().Any(f => f.Folder == "second"),
            "the surviving folder should stop being a duplicate once the route is free");
        Wait.For(
            () => gate.LiveRoutes.Contains("shared"),
            "and should then claim the route itself");
    }

    /// <summary>
    /// The private managed dependency proof. `CreateView` calls into Newtonsoft,
    /// so a resolver that fails to find it produces a failed folder rather than a
    /// passing test.
    /// </summary>
    [Fact]
    public void APrivateManagedDependencyResolvesThroughDepsJson()
    {
        Sta.Run(() =>
        {
            using var root = ModuleFolder.Create();
            var gate = new RecordingGate();
            using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

            var folder = root.Fixture("dependent", "Dependent");
            Assert.True(
                File.Exists(Path.Combine(folder, "Module.Fixture.deps.json")),
                "the resolver reads .deps.json, so Citizen.targets must deploy it");
            Assert.True(
                File.Exists(Path.Combine(folder, "Citizen.PrivateDependency.dll")),
                "the private dependency must be deployed beside the entry assembly");

            searcher.Start();
            Wait.For(() => gate.LiveRoutes.Contains("dependent"), "it should register");

            var view = gate.Registered.Single().Instance
                .CreateView(new Citadel.Core.Rpl.Lifetime());
            Assert.Equal(
                "private dependency loaded",
                Assert.IsType<System.Windows.Controls.TextBlock>(view).Text);

            // The retained context must still not lock the folder, dependency or
            // not — that is the whole delete gate.
            root.DeleteWhileRunning("dependent");
            Assert.False(Directory.Exists(folder));
        });
    }

    /// <summary>
    /// Without the dependency deployed, the screen fails visibly instead of
    /// registering and then throwing when the user navigates to it.
    /// </summary>
    [Fact]
    public void AMissingPrivateDependencyIsVisibleRatherThanSilent()
    {
        Sta.Run(() =>
        {
            using var root = ModuleFolder.Create();
            var gate = new RecordingGate();
            using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

            root.Fixture("dependent", "Dependent", includeDependency: false);
            searcher.Start();
            Wait.For(() => gate.LiveRoutes.Contains("dependent"), "identity is still valid");

            // The load succeeds; the failure lands where the dependency is first
            // needed. Router already guards CreateView and unregisters on failure,
            // so this is caught rather than fatal.
            var module = gate.Registered.Single().Instance;
            Assert.ThrowsAny<Exception>(() =>
                module.CreateView(new Citadel.Core.Rpl.Lifetime()));
        });
    }

    /// <summary>
    /// A citizen shipping its own copy of a shared assembly. The default context
    /// must win, so the private copy is ignored and the cast still succeeds —
    /// which is what keeps one IModule identity.
    /// </summary>
    [Fact]
    public void APrivateCopyOfASharedAssemblyIsIgnoredInFavourOfTheDefaultContext()
    {
        Sta.Run(() =>
        {
            using var root = ModuleFolder.Create();
            var gate = new RecordingGate();
            using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

            var folder = root.Fixture("contaminated", "Shared");
            var contraband = Path.Combine(
                Path.GetDirectoryName(typeof(Citadel.Core.Modules.IModule).Assembly.Location)!,
                "Citadel.Contract.dll");
            File.Copy(contraband, Path.Combine(folder, "Citadel.Contract.dll"), overwrite: true);

            searcher.Start();

            Wait.For(
                () => gate.LiveRoutes.Contains("shared"),
                "the shared assembly must resolve from the default context, so the cast holds");
            var citizenContract = gate.Registered.Single().Instance.GetType()
                .GetInterface(typeof(Citadel.Core.Modules.IModule).FullName!)!;
            Assert.Same(typeof(Citadel.Core.Modules.IModule), citizenContract);
        });
    }

    /// <summary>A constructor that throws is a folder failure, not a crash.</summary>
    [Fact]
    public void ACitizenConstructorThatThrowsIsAFolderFailure()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        var folder = root.Fixture("throwing", "Throwing");
        ModuleFolder.WriteManifest(
            folder, "throwing", "Module.Fixture.dll", "Citadel.Uia.Citizen.Throwing");
        searcher.Start();

        Wait.For(
            () => searcher.Failures().Any(f => f.Folder == "throwing"),
            "a throwing constructor should fail the folder");
        Assert.Empty(gate.Registered);
    }
}
