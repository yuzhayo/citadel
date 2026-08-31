using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Module.Mangareader.ShareLogic;

public sealed record ReleasedArchiveProcess(int ProcessId, string DisplayName);

/// <summary>
/// Resolves Windows file-sharing conflicts immediately before an atomic CBZ
/// replacement. Restart Manager is part of Windows, so ordinary CBZ editing
/// does not require a private archive dependency in the citizen.
/// </summary>
public sealed class ArchiveLockCoordinator
{
    private const int ReleaseWaitMilliseconds = 5_000;
    private const int ReleasePollMilliseconds = 100;

    public IReadOnlyList<ReleasedArchiveProcess> ReleaseForReplacement(
        string archivePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        var released = new Dictionary<int, ReleasedArchiveProcess>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = RestartManagerSession.ForFile(fullPath);
            var lockers = session.GetLockers();
            if (lockers.Count == 0) return released.Values.ToArray();

            var protectedLockers = lockers
                .Where(IsProtectedExternalProcess)
                .ToArray();
            if (protectedLockers.Length > 0)
            {
                throw new IOException(
                    $"The chapter is locked by a protected Windows process: "
                    + FormatLockers(protectedLockers));
            }

            session.ProtectCurrentProcess();
            foreach (var locker in lockers.Where(locker =>
                         locker.ProcessId == Environment.ProcessId))
            {
                session.Protect(locker);
            }

            var externalLockers = lockers
                .Where(locker => locker.ProcessId != Environment.ProcessId)
                .ToArray();
            if (externalLockers.Length > 0)
            {
                session.ShutdownLockers();
                foreach (var locker in externalLockers)
                {
                    released[locker.ProcessId] = new ReleasedArchiveProcess(
                        locker.ProcessId,
                        locker.DisplayName);
                }
            }

            if (WaitUntilExclusive(fullPath, cancellationToken))
            {
                return released.Values.ToArray();
            }
        }

        using var finalSession = RestartManagerSession.ForFile(fullPath);
        var remaining = finalSession.GetLockers();
        var detail = remaining.Count == 0
            ? "Windows did not report the remaining owner."
            : FormatLockers(remaining);
        throw new IOException($"The chapter remained locked after release attempts: {detail}");
    }

    private static bool WaitUntilExclusive(
        string path,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + ReleaseWaitMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(ReleasePollMilliseconds);
        }

        return false;
    }

    private static bool IsProtectedExternalProcess(LockingProcess locker) =>
        locker.ProcessId != Environment.ProcessId
        && locker.ApplicationType is RestartManagerApplicationType.Critical
            or RestartManagerApplicationType.Explorer
            or RestartManagerApplicationType.Service;

    private static string FormatLockers(IEnumerable<LockingProcess> lockers) =>
        string.Join(
            ", ",
            lockers.Select(locker => $"{locker.DisplayName} (PID {locker.ProcessId})"));

    private sealed class RestartManagerSession : IDisposable
    {
        private const int ErrorSuccess = 0;
        private const int ErrorMoreData = 234;
        private const int ErrorFailShutdown = 351;
        private const uint ForceShutdown = 0x1;
        private const RestartManagerFilterAction NoShutdown =
            RestartManagerFilterAction.NoShutdown;

        private readonly uint _handle;
        private bool _disposed;

        private RestartManagerSession(uint handle) => _handle = handle;

        public static RestartManagerSession ForFile(string path)
        {
            var result = NativeMethods.RmStartSession(
                out var handle,
                0,
                Guid.NewGuid().ToString("N"));
            ThrowIfFailed(result, "start a Windows Restart Manager session");

            var session = new RestartManagerSession(handle);
            try
            {
                result = NativeMethods.RmRegisterResources(
                    handle,
                    1,
                    new[] { path },
                    0,
                    IntPtr.Zero,
                    0,
                    null);
                ThrowIfFailed(result, "register the locked chapter");
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public IReadOnlyList<LockingProcess> GetLockers()
        {
            uint required = 0;
            uint count = 0;
            uint rebootReasons = 0;
            var result = NativeMethods.RmGetList(
                _handle,
                out required,
                ref count,
                null,
                ref rebootReasons);
            if (result == ErrorSuccess) return Array.Empty<LockingProcess>();
            if (result != ErrorMoreData)
                ThrowIfFailed(result, "query processes locking the chapter");

            while (true)
            {
                var processes = new RestartManagerProcessInfo[required];
                count = required;
                result = NativeMethods.RmGetList(
                    _handle,
                    out required,
                    ref count,
                    processes,
                    ref rebootReasons);
                if (result == ErrorMoreData) continue;
                ThrowIfFailed(result, "read processes locking the chapter");

                return processes
                    .Take((int)count)
                    .Select(ToLockingProcess)
                    .ToArray();
            }
        }

        public void ProtectCurrentProcess()
        {
            using var current = Process.GetCurrentProcess();
            if (!NativeMethods.GetProcessTimes(
                    current.Handle,
                    out var started,
                    out _,
                    out _,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not identify the current Citadel process.");
            }

            var process = new RestartManagerUniqueProcess
            {
                ProcessId = Environment.ProcessId,
                ProcessStartTime = started,
            };
            var result = NativeMethods.RmAddFilterByProcess(
                _handle,
                null,
                ref process,
                null,
                NoShutdown);
            ThrowIfFailed(result, "protect Citadel from lock resolution");
        }

        public void Protect(LockingProcess locker)
        {
            var process = locker.NativeProcess;
            var result = NativeMethods.RmAddFilterByProcess(
                _handle,
                null,
                ref process,
                null,
                NoShutdown);
            ThrowIfFailed(result, "protect Citadel from lock resolution");
        }

        public void ShutdownLockers()
        {
            var result = NativeMethods.RmShutdown(
                _handle,
                ForceShutdown,
                IntPtr.Zero);
            if (result != ErrorSuccess && result != ErrorFailShutdown)
                ThrowIfFailed(result, "release applications locking the chapter");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NativeMethods.RmEndSession(_handle);
        }

        private static LockingProcess ToLockingProcess(RestartManagerProcessInfo process)
        {
            var processId = process.Process.ProcessId;
            var displayName = process.ApplicationName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                try
                {
                    displayName = Process.GetProcessById(processId).ProcessName;
                }
                catch (ArgumentException)
                {
                    displayName = "Process";
                }
            }

            return new LockingProcess(
                processId,
                displayName,
                process.ApplicationType,
                process.Process);
        }

        private static void ThrowIfFailed(int result, string operation)
        {
            if (result == ErrorSuccess) return;
            throw new Win32Exception(result, $"Could not {operation}.");
        }
    }

    private sealed record LockingProcess(
        int ProcessId,
        string DisplayName,
        RestartManagerApplicationType ApplicationType,
        RestartManagerUniqueProcess NativeProcess);

    private enum RestartManagerApplicationType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000,
    }

    private enum RestartManagerFilterAction
    {
        Invalid = 0,
        NoRestart = 1,
        NoShutdown = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RestartManagerUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestartManagerProcessInfo
    {
        public RestartManagerUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public RestartManagerApplicationType ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    private static class NativeMethods
    {
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmStartSession(
            out uint sessionHandle,
            int sessionFlags,
            string sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmRegisterResources(
            uint sessionHandle,
            uint fileCount,
            string[] fileNames,
            uint applicationCount,
            IntPtr applications,
            uint serviceCount,
            string[]? serviceNames);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmGetList(
            uint sessionHandle,
            out uint processInfoNeeded,
            ref uint processInfoCount,
            [In, Out] RestartManagerProcessInfo[]? affectedApplications,
            ref uint rebootReasons);

        [DllImport(
            "rstrtmgr.dll",
            EntryPoint = "RmAddFilter",
            CharSet = CharSet.Unicode)]
        public static extern int RmAddFilterByProcess(
            uint sessionHandle,
            string? moduleName,
            ref RestartManagerUniqueProcess process,
            string? serviceShortName,
            RestartManagerFilterAction filterAction);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmShutdown(
            uint sessionHandle,
            uint actionFlags,
            IntPtr statusCallback);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmEndSession(uint sessionHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(
            IntPtr processHandle,
            out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME userTime);
    }
}
