using System.IO;
using Citadel.Searcher;

namespace Citadel.Uia;

/// <summary>
/// The promise this stage exists to prove: a folder appears and a screen appears;
/// the folder goes and the screen goes. Plus the races that make it hard —
/// half-copied folders, two folders claiming one route, rename, and delete
/// between the event and the read.
///
/// Real filesystem throughout. A mock would pass while the real thing fails.
/// </summary>
public class SearcherDiscoveryTests
{
    private static readonly string[] Reserved =
    [
        "settings",
        "settings/appearance",
        "settings/layout",
        "settings/gallery",
    ];

    /// <summary>
    /// A clean install has no citizens, and `FileSystemWatcher` throws on a
    /// missing path — so the searcher creates the root instead of failing
    /// startup.
    /// </summary>
    [Fact]
    public void AMissingRootIsCreatedRatherThanFailingStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "citadel-absent-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(root));

        var gate = new RecordingGate();
        using var searcher = new Watcher(root, gate, Reserved, _ => { });
        searcher.Start();

        try
        {
            Assert.True(Directory.Exists(root));
            Assert.Empty(searcher.Failures());
            Assert.Empty(gate.Registered);
        }
        finally
        {
            searcher.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A folder dropped while running appears, with nothing pressed.</summary>
    [Fact]
    public void AFolderDroppedWhileRunningRegistersWithoutUpdateModules()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });
        searcher.Start();

        Wait.For(() => Directory.Exists(root.Root), "the root should exist");
        Assert.Empty(gate.Registered);

        root.Fixture("dropped", "Shared");

        Wait.For(
            () => gate.LiveRoutes.Contains("shared"),
            "the watcher should register a folder dropped after Start, with no manual rescan");
    }

    /// <summary>
    /// The initial scan finds what is already there, through the same path, and
    /// all six manifest fields survive translation to a descriptor.
    /// </summary>
    [Fact]
    public void AFolderPresentBeforeStartupRegistersWithEveryManifestField()
    {
        using var root = ModuleFolder.Create();
        var folder = root.Fixture(
            "present",
            "Shared",
            layout: """{ "slots": { "Message": { "kind": "visibility", "visible": true } } }""");
        File.WriteAllText(Path.Combine(folder, "module.json"), """
            {
              "title": "Present",
              "icon": "",
              "route": "shared",
              "order": 42,
              "entry": "Module.Fixture.dll",
              "type": "Citadel.Uia.Citizen.Shared"
            }
            """);

        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });
        searcher.Start();

        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the initial scan should find it");
        var descriptor = gate.Registered.Single();
        Assert.Equal("Present", descriptor.Title);
        Assert.Equal("", descriptor.Icon);
        Assert.Equal(42, descriptor.Order);
        Assert.NotNull(descriptor.Layout);
        Assert.Equal(["Message"], descriptor.Layout!.SlotNames);
    }

    /// <summary>Delete the folder and its route disappears.</summary>
    [Fact]
    public void DeletingAFolderUnregistersItsRoute()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("doomed", "Shared");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");

        root.DeleteWhileRunning("doomed");

        Wait.For(() => gate.Unregistered.Contains("shared"), "deleting the folder should unregister");
        Assert.Empty(gate.LiveRoutes);
    }

    /// <summary>
    /// A folder that arrives without its DLL is visibly retried, then settles
    /// into a failure. The retry must be observable or the gate cannot be
    /// asserted at all.
    /// </summary>
    [Fact]
    public void AHalfCopiedFolderRetriesVisiblyThenSettles()
    {
        using var root = ModuleFolder.Create();
        var log = new List<string>();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, line =>
        {
            lock (log) log.Add(line);
        });
        searcher.Start();

        // Manifest first, DLL never: exactly what a copy in progress looks like.
        var folder = root.Folder("arriving");
        ModuleFolder.WriteManifest(
            folder, "arriving", "Module.Fixture.dll", "Citadel.Uia.Citizen.Arriving");

        Wait.For(
            () =>
            {
                lock (log) return log.Count(line => line.Contains("attempt")) >= 2;
            },
            "each retry attempt should be logged with its number");

        Wait.For(
            () => searcher.Failures().Any(f =>
                f.Folder == "arriving" && !f.Message.Contains("retrying")),
            "after the retry budget the failure should settle");

        lock (log)
        {
            Assert.Contains(log, line => line.Contains("attempt 1/"));
            Assert.Contains(log, line => line.Contains("attempt 2/"));
        }
    }

    /// <summary>
    /// Complete the copy and the same reconcile path picks
    /// it up. No separate "finish the copy" code path exists to get wrong.
    /// </summary>
    [Fact]
    public void CompletingAHalfCopiedFolderRegistersThroughTheSamePath()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });
        searcher.Start();

        var folder = root.Folder("arriving");
        ModuleFolder.WriteManifest(
            folder, "arriving", "Module.Fixture.dll", "Citadel.Uia.Citizen.Arriving");
        Wait.For(
            () => searcher.Failures().Any(f => f.Folder == "arriving"),
            "the incomplete folder should be visible as a problem first");

        foreach (var file in Directory.EnumerateFiles(ModuleFolder.FixtureOutput))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }

        Wait.For(() => gate.LiveRoutes.Contains("arriving"), "completing the copy should register it");
        Wait.For(
            () => !searcher.Failures().Any(f => f.Folder == "arriving"),
            "a repaired folder should stop being listed as failed");
    }

    /// <summary>
    /// Found by driving the real app, not by reading the code: completing a
    /// half-copy touched a folder that had *already* registered, so the reconcile
    /// pass loaded it a second time and the gate reported a duplicate route
    /// against a folder that was its own only claimant. Each extra pass also
    /// retained another load context.
    /// </summary>
    [Fact]
    public void TouchingAnAlreadyRegisteredFolderDoesNotRegisterItTwice()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        var folder = root.Fixture("settled", "Shared");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");

        // What finishing a copy looks like: files land in a folder that is
        // already a healthy citizen.
        foreach (var file in Directory.EnumerateFiles(ModuleFolder.FixtureOutput))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }
        Thread.Sleep(1500);

        Assert.Single(gate.Registered);
        Assert.Empty(searcher.Failures());
    }

    /// <summary>
    /// The other half of the same rule: a changed DLL is picked up next launch,
    /// not live. The screen stays the one already loaded.
    /// </summary>
    [Fact]
    public void AChangedEntryAssemblyIsNotReloadedInThisProcess()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        var folder = root.Fixture("changing", "Shared");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");
        var loaded = gate.Registered.Single().Instance;

        // Rewriting the entry assembly is what a rebuild deploys. Nothing about
        // the live registration may change.
        var entry = Path.Combine(folder, "Module.Fixture.dll");
        File.SetLastWriteTimeUtc(entry, DateTime.UtcNow);
        File.Copy(
            Path.Combine(ModuleFolder.FixtureOutput, "Module.Fixture.dll"),
            entry,
            overwrite: true);
        Thread.Sleep(1500);

        Assert.Single(gate.Registered);
        Assert.Same(loaded, gate.Registered.Single().Instance);
        Assert.Empty(gate.Unregistered);
    }

    /// <summary>
    /// A permanently broken citizen is a visible failure and nothing else
    /// breaks — a healthy folder beside it still registers.
    /// </summary>
    [Fact]
    public void ABrokenFolderIsIsolatedFromAHealthyOne()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Folder("broken", ("module.json", "{ \"title\": \"Broken\" }"));
        root.Fixture("healthy", "Shared");
        searcher.Start();

        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the healthy folder should register");
        Wait.For(
            () => searcher.Failures().Any(f =>
                f.Folder == "broken" && f.Stage == SearchStage.Manifest),
            "the broken folder should be a visible manifest failure");
        Assert.Single(gate.Registered);
    }

    /// <summary>Deleting a broken folder clears its failure entry.</summary>
    [Fact]
    public void DeletingABrokenFolderClearsItsFailure()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Folder("broken", ("module.json", "{ }"));
        searcher.Start();
        Wait.For(() => searcher.Failures().Any(f => f.Folder == "broken"), "it should fail first");

        root.DeleteWhileRunning("broken");

        Wait.For(() => searcher.Failures().Count == 0, "deleting it should clear the failure");
    }

    /// <summary>
    /// Fail-soft layout: the screen registers with no editable slots, and the
    /// reason is listed rather than silently swallowed.
    /// </summary>
    [Fact]
    public void AMalformedLayoutRegistersTheScreenAndListsTheWarning()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("badlayout", "Shared", layout: "{ not json");
        searcher.Start();

        Wait.For(() => gate.LiveRoutes.Contains("shared"), "identity is valid, so it registers");
        Assert.Null(gate.Registered.Single().Layout);
        Wait.For(
            () => searcher.Failures().Any(f =>
                f.Folder == "badlayout" && f.Stage == SearchStage.Layout),
            "the layout problem should still be visible");
    }

    /// <summary>A folder without a manifest is skipped, not failed.</summary>
    [Fact]
    public void AFolderWithNoManifestIsSkippedSilently()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Folder("notacitizen", ("notes.txt", "not a screen"));
        root.Fixture("real", "Shared");
        searcher.Start();

        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the real citizen should register");
        Assert.DoesNotContain(searcher.Failures(), f => f.Folder == "notacitizen");
    }

    /// <summary>`Update modules` re-raises the same scan; it is not a second path.</summary>
    [Fact]
    public void UpdateModulesRescansThroughTheSameReconcile()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });
        searcher.Start();
        Wait.For(() => Directory.Exists(root.Root), "the root should exist");

        // Copied with the watcher's events deliberately ignored by requesting a
        // rescan immediately: whichever wins, one register is the result.
        root.Fixture("rescanned", "Shared");
        searcher.RequestRediscovery();

        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the manual rescan should find it");
        Assert.Single(gate.Registered);
    }

    /// <summary>
    /// A folder deleted between the watcher event and the read must be harmless.
    /// Reproduced by deleting immediately after creating, inside the debounce.
    /// </summary>
    [Fact]
    public void AFolderDeletedBetweenEventAndReadIsHarmless()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });
        searcher.Start();
        Wait.For(() => Directory.Exists(root.Root), "the root should exist");

        root.Fixture("fleeting", "Shared");
        root.DeleteWhileRunning("fleeting");

        // Nothing registers and nothing throws; the pump survives to serve the
        // next folder, which is the real assertion.
        root.Fixture("surviving", "Shared");
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the searcher should still be working");
        Assert.Empty(gate.Unregistered);
    }

    /// <summary>A renamed folder is reconciled: the old route goes, the new one arrives.</summary>
    [Fact]
    public void ARenamedFolderMovesItsRegistration()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("first", "First");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("first"), "the original should register");

        Directory.Move(Path.Combine(root.Root, "first"), Path.Combine(root.Root, "second"));
        ModuleFolder.WriteManifest(
            Path.Combine(root.Root, "second"),
            "second",
            "Module.Fixture.dll",
            "Citadel.Uia.Citizen.Second");

        Wait.For(
            () => gate.LiveRoutes.Contains("second") && !gate.LiveRoutes.Contains("first"),
            "the rename should move the registration rather than leaving both");
    }

    /// <summary>
    /// Re-created with a different route: the old route must not outlive the
    /// folder that declared it. This is the check-gated-on-presence case.
    /// </summary>
    [Fact]
    public void RecreatingAFolderWithADifferentRouteDropsTheOldOne()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        var folder = root.Fixture("screen", "Alpha");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("alpha"), "alpha should register");

        ModuleFolder.WriteManifest(folder, "beta", "Module.Fixture.dll", "Citadel.Uia.Citizen.Beta");

        Wait.For(
            () => gate.LiveRoutes.Contains("beta") && !gate.LiveRoutes.Contains("alpha"),
            "the old route should be unregistered when the folder changes route");
    }

    /// <summary>Twenty folders at once. Serialization must not lose any of them.</summary>
    [Fact]
    public void ManyFoldersArrivingTogetherAllRegister()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });
        searcher.Start();
        Wait.For(() => Directory.Exists(root.Root), "the root should exist");

        for (var index = 1; index <= 20; index++)
        {
            root.Fixture($"screen{index:00}", $"Screen{index:00}");
        }

        Wait.For(
            () => gate.LiveRoutes.Count == 20,
            $"all twenty should register; saw {gate.LiveRoutes.Count}",
            30000);
    }

    /// <summary>Unregistering a route that was never registered is not a failure.</summary>
    [Fact]
    public void AFolderThatNeverRegisteredIsDeletedWithoutUnregistering()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Folder("broken", ("module.json", "{ }"));
        searcher.Start();
        Wait.For(() => searcher.Failures().Any(f => f.Folder == "broken"), "it should fail first");

        root.DeleteWhileRunning("broken");
        Wait.For(() => searcher.Failures().Count == 0, "the failure should clear");

        Assert.Empty(gate.Unregistered);
    }

    /// <summary>
    /// Dispose must be deterministic and bounded: watchers detached, pump
    /// stopped, no further gate traffic from a folder that arrives afterwards.
    /// </summary>
    [Fact]
    public void DisposeStopsAcceptingWatcherEvents()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("early", "Shared");
        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");

        searcher.Dispose();
        var registeredAtDispose = gate.Registered.Count;

        // A distinct route, so anything registering afterwards is unmistakable.
        root.Fixture("late", "First");
        Thread.Sleep(1200);

        Assert.Equal(registeredAtDispose, gate.Registered.Count);
        Assert.DoesNotContain("first", gate.LiveRoutes);
    }
}
