using System.IO;
using System.Runtime.InteropServices;
using Velopack;

namespace Citadel.Shell;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before the owner gate: install/update/uninstall hooks launch
        // this EXE with fast-exit arguments that Velopack owns.
        VelopackApp.Build().Run();

        var executablePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Citadel.Shell.exe");
        return Run(args, executablePath, RunOwner, ShowStartupError);
    }

    internal static int Run(
        string[] args,
        string executablePath,
        Func<SingleInstanceHost, int> runOwner,
        Action<string> reportError,
        TimeSpan? contactTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runOwner);
        ArgumentNullException.ThrowIfNull(reportError);

        var launch = SingleInstanceHost.Start(args, executablePath, contactTimeout);
        if (launch.Kind != InstanceLaunchKind.Owner)
        {
            if (launch.Error is not null) reportError(launch.Error);
            return launch.ExitCode;
        }

        using var owner = launch.Owner!;
        return runOwner(owner);
    }

    private static int RunOwner(SingleInstanceHost owner)
    {
        var app = new App(owner);
        app.InitializeComponent();
        return app.Run();
    }

    private static void ShowStartupError(string message) => MessageBox(
        0,
        message,
        "Citadel",
        0x00000010);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(
        nint owner,
        string text,
        string caption,
        uint type);
}
