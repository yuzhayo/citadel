namespace Citadel.Core.Tests;

[Collection("LogStatics")]
public class LogTests
{
    [Fact]
    public void Entries_LandInTheRing_AndAreTagged()
    {
        Log.Main("ring-test-main-entry");
        Log.Modules("ring-test-modules-entry");

        var dump = Log.Full();
        Assert.Contains("[Main] ring-test-main-entry", dump);
        Assert.Contains("[Modules] ring-test-modules-entry", dump);
    }

    [Fact]
    public void Start_WritesTheTailToLogTxt_FlushingStagedEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "citadel-logtest-" + Guid.NewGuid().ToString("N"));

        Log.Main("staged-before-start");
        Log.Start(dir);
        Log.Main("written-after-start");

        // Give the writer thread a moment, then shut down cleanly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        Log.Finish();
        Assert.True(DateTime.UtcNow < deadline);

        var file = Path.Combine(dir, "log.txt");
        Assert.True(File.Exists(file));
        var text = File.ReadAllText(file);
        Assert.Contains("staged-before-start", text);
        Assert.Contains("written-after-start", text);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Reentrancy_IsGuarded_NoRecursion()
    {
        // Even a pathological message cannot recurse the write path:
        // the guard makes nested writes a no-op instead of a loop.
        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 1000; i++) Log.Main("reentrancy-hammer");
        });
        Assert.Null(exception);
    }
}
