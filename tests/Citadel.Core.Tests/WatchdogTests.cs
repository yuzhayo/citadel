using System.Diagnostics;
using Citadel.Core.Crl;

namespace Citadel.Core.Tests;

[Collection("LogStatics")]
public class WatchdogTests
{
    /// <summary>
    /// Log.Full() is a process-wide ring, so every assertion here has to look
    /// only at what this test appended. Reading the whole ring passes on a
    /// sibling test's output.
    /// </summary>
    private static string Since(int mark)
    {
        var full = Log.Full();
        return full[Math.Min(mark, full.Length)..];
    }

    /// <summary>
    /// A queue whose wake never drains: the ping is never answered, so only the
    /// timer side can notice. The wording is asserted deliberately — "ping
    /// unanswered" proves the report came from the timer, which is the path
    /// that survives a fully wedged main thread.
    /// </summary>
    [Fact]
    public void WedgedMain_IsDetectedFromTheTimerSide()
    {
        var mark = Log.Full().Length;
        var wedged = new MainQueue(_ => { });
        using var watchdog = new Watchdog(wedged, stallMs: 10);

        watchdog.Start(intervalMs: 50);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Since(mark).Contains("ping unanswered")) break;
            Thread.Sleep(50);
        }

        Assert.Contains("main-thread stall", Since(mark));
        Assert.Contains("ping unanswered", Since(mark));
    }

    /// <summary>
    /// Regression. The interval MUST exceed the stall threshold here, because
    /// that is the production shape. Measuring "time since the last pong" rather
    /// than ping→pong
    /// latency reports a stall on every tick under those settings, since a
    /// healthy pong is always about one interval old.
    /// </summary>
    [Fact]
    public void HealthyDrain_IsQuiet_WhenIntervalExceedsThreshold()
    {
        var mark = Log.Full().Length;
        var time = new ManualTimeProvider();
        var main = new FakeMain();
        using var watchdog = new Watchdog(main.Queue, stallMs: 20, time);

        watchdog.Poll();
        main.Pump();
        time.Advance(TimeSpan.FromMilliseconds(200));
        watchdog.Poll();
        main.Pump();

        Assert.DoesNotContain("main-thread stall", Since(mark));
    }

    /// <summary>
    /// A slow-but-alive main thread is reported by the pong, not the timer:
    /// the ping is answered, just late.
    /// </summary>
    [Fact]
    public void SlowButAliveMain_IsReportedByThePong()
    {
        var mark = Log.Full().Length;
        var main = new FakeMain();
        using var watchdog = new Watchdog(main.Queue, stallMs: 30);

        watchdog.Start(intervalMs: 100);

        Thread.Sleep(200);   // let a ping sit unanswered past the threshold
        main.Pump();         // then answer it

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (Since(mark).Contains("main-thread stall")) break;
            Thread.Sleep(20);
        }

        Assert.Contains("main-thread stall", Since(mark));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            _timestamp = checked(_timestamp + duration.Ticks);
        }
    }
}
