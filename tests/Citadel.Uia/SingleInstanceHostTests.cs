using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Citadel.Shell;

namespace Citadel.Uia;

public class SingleInstanceHostTests
{
    [Fact]
    public void OwnerRepliesStartingThenReadyWithoutAnImplicitZeroHandle()
    {
        var executablePath = UniqueExecutablePath();
        var first = SingleInstanceHost.Start([], executablePath);
        Assert.Equal(InstanceLaunchKind.Owner, first.Kind);
        using var owner = Assert.IsType<SingleInstanceHost>(first.Owner);

        var starting = StartOnSecondaryThread(executablePath);

        Assert.Equal(InstanceLaunchKind.Starting, starting.Kind);
        Assert.Equal(Environment.ProcessId, starting.OwnerProcessId);
        Assert.Equal(0, starting.WindowHandle);

        var showRequests = 0;
        owner.MarkReady(new nint(123), _ =>
        {
            Interlocked.Increment(ref showRequests);
            return Task.FromResult(true);
        });
        Assert.Equal(1, Volatile.Read(ref showRequests));

        var ready = StartOnSecondaryThread(executablePath);

        Assert.Equal(InstanceLaunchKind.Ready, ready.Kind);
        Assert.Equal(Environment.ProcessId, ready.OwnerProcessId);
        Assert.Equal(new nint(123), ready.WindowHandle);
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref showRequests) == 2,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SecondaryProgramPathNeverInvokesTheWpfOwnerCallback()
    {
        var executablePath = UniqueExecutablePath();
        var first = SingleInstanceHost.Start([], executablePath);
        using var owner = Assert.IsType<SingleInstanceHost>(first.Owner);
        var ownerCallbackInvoked = false;
        string? error = null;

        var exitCode = Program.Run(
            [],
            executablePath,
            _ =>
            {
                ownerCallbackInvoked = true;
                return 0;
            },
            message => error = message);

        Assert.Equal(0, exitCode);
        Assert.False(ownerCallbackInvoked);
        Assert.Null(error);
    }

    [Fact]
    public void ResetUiIsRefusedWhileAnOwnerExists()
    {
        var executablePath = UniqueExecutablePath();
        var first = SingleInstanceHost.Start([], executablePath);
        using var owner = Assert.IsType<SingleInstanceHost>(first.Owner);
        var ownerCallbackInvoked = false;
        string? error = null;

        var exitCode = Program.Run(
            ["--reset-ui"],
            executablePath,
            _ =>
            {
                ownerCallbackInvoked = true;
                return 0;
            },
            message => error = message);

        Assert.Equal(1, exitCode);
        Assert.False(ownerCallbackInvoked);
        Assert.Equal(
            "Citadel is already running. Exit Citadel before --reset-ui.",
            error);
    }

    [Fact]
    public void BrokenClientDoesNotStopLaterActivations()
    {
        var executablePath = UniqueExecutablePath();
        var first = SingleInstanceHost.Start([], executablePath);
        using var owner = Assert.IsType<SingleInstanceHost>(first.Owner);
        owner.MarkReady(new nint(123), _ => Task.FromResult(true));

        var identity = SingleInstanceHost.Identity(
            executablePath,
            Process.GetCurrentProcess().SessionId);
        using (var broken = new NamedPipeClientStream(
            ".",
            $"Citadel.{identity}.Activation",
            PipeDirection.InOut,
            PipeOptions.None))
        {
            broken.Connect(1000);
        }

        var ready = StartOnSecondaryThread(executablePath);

        Assert.Equal(InstanceLaunchKind.Ready, ready.Kind);
        Assert.Equal(new nint(123), ready.WindowHandle);
    }

    [Fact]
    public void ReadyIsNotReturnedWhenOwnerCannotShowTheWindow()
    {
        var executablePath = UniqueExecutablePath();
        var first = SingleInstanceHost.Start([], executablePath);
        using var owner = Assert.IsType<SingleInstanceHost>(first.Owner);
        owner.MarkReady(new nint(123), _ => Task.FromResult(false));

        var result = StartOnSecondaryThread(executablePath);

        Assert.Equal(InstanceLaunchKind.Failed, result.Kind);
        Assert.Contains("activation-failed", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyWaitsUntilOwnerShowCompletes()
    {
        var executablePath = UniqueExecutablePath();
        var first = SingleInstanceHost.Start([], executablePath);
        using var owner = Assert.IsType<SingleInstanceHost>(first.Owner);
        using var entered = new ManualResetEventSlim();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.MarkReady(new nint(123), _ =>
        {
            entered.Set();
            return release.Task;
        });

        InstanceLaunch? result = null;
        var secondary = new Thread(() => result = SingleInstanceHost.Start([], executablePath));
        try
        {
            secondary.Start();

            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
            Assert.False(secondary.Join(TimeSpan.Zero));
        }
        finally
        {
            release.TrySetResult(true);
        }
        Assert.True(secondary.Join(TimeSpan.FromSeconds(1)));

        Assert.Equal(InstanceLaunchKind.Ready, Assert.IsType<InstanceLaunch>(result).Kind);
    }

    [Fact]
    public void IdentitySeparatesWindowsSessions()
    {
        var executablePath = UniqueExecutablePath();

        var first = SingleInstanceHost.Identity(executablePath, sessionId: 41);
        var same = SingleInstanceHost.Identity(executablePath, sessionId: 41);
        var otherSession = SingleInstanceHost.Identity(executablePath, sessionId: 42);

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherSession);
    }

    private static string UniqueExecutablePath() => Path.Combine(
        Path.GetTempPath(),
        "citadel-instance-tests",
        Guid.NewGuid().ToString("N"),
        "Citadel.Shell.exe");

    private static InstanceLaunch StartOnSecondaryThread(string executablePath)
    {
        InstanceLaunch? result = null;
        var thread = new Thread(() =>
            result = SingleInstanceHost.Start([], executablePath));
        thread.Start();
        thread.Join();
        return Assert.IsType<InstanceLaunch>(result);
    }
}
