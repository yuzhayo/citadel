using System.Diagnostics;
using System.IO;
using Citadel.Searcher;

namespace Citadel.Uia;

/// <summary>
/// Idle must be free. Nothing polls, and the resident lifecycle makes this
/// load-bearing: the app
/// sits hidden in the tray for hours.
///
/// Found by measuring the real app, not by reading the code. The pump's wait was
/// guarded with `if (wait > TimeSpan.Zero)`, which skipped the wait entirely for
/// `Timeout.InfiniteTimeSpan` (-1ms) — so with nothing pending the loop spun a
/// core flat. Two citizens loaded, 20 seconds observed, ~20 seconds of CPU
/// consumed.
/// </summary>
[Collection("Shell power saving serial")]
public class SearcherIdleCostTests
{
    private static readonly string[] Reserved = ["settings"];

    [Fact]
    public void AnIdleSearcherConsumesEssentiallyNoCpu()
    {
        using var root = ModuleFolder.Create();
        var gate = new RecordingGate();
        using var searcher = new Watcher(root.Root, gate, Reserved, _ => { });

        root.Fixture("resident", "Shared");

        // Hosted runners charge CLR, antivirus, and xUnit work to testhost. Use
        // the same process with no pump as the control instead of pretending
        // that all process CPU belongs to Searcher.
        var process = Process.GetCurrentProcess();
        var baseline = MeasureCpu(process);

        searcher.Start();
        Wait.For(() => gate.LiveRoutes.Contains("shared"), "the citizen should register");

        // Let the post-registration reconcile settle before measuring.
        Thread.Sleep(500);

        var consumed = MeasureCpu(process);
        var searcherCost = consumed - baseline;

        // A spinning pump burns a full core: ~2000ms per 2000ms of wall clock.
        // The relative 750ms ceiling still catches that by a wide margin while
        // allowing the shared runner's background cost to cancel out.
        Assert.True(
            searcherCost < TimeSpan.FromMilliseconds(750),
            $"an idle searcher added {searcherCost.TotalMilliseconds:F0}ms CPU "
            + $"over a {baseline.TotalMilliseconds:F0}ms testhost baseline in 2s; "
            + "the pump appears to be spinning instead of waiting");
    }

    private static TimeSpan MeasureCpu(Process process)
    {
        process.Refresh();
        var before = process.TotalProcessorTime;
        Thread.Sleep(2000);
        process.Refresh();
        return process.TotalProcessorTime - before;
    }
}
