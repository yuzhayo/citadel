using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CitadelBridge;

/// <summary>
/// One idempotent runtime bootstrap chain (plan §6.4):
///
///   system Python ≥3.12 → else vendored CPython NuGet 3.12.10 (SHA-256
///   verified BEFORE extraction) → shared venv → pip requirements →
///   camoufox browser fetch → caller verifies via PyHost.PingAsync.
///
/// Every stage is a no-op when already satisfied, so "retry" simply re-runs
/// the chain and resumes at the first incomplete stage. No admin, no system
/// PATH mutation: everything rebuildable lives under the runtime root.
///
/// Shared SOURCE (module/sharedLogic/cs): any citizen may run setup/checks
/// without depending on another screen. WPF-free by design.
/// </summary>
public sealed class RuntimeSetup
{
    // Pinned at implementation time: the .nupkg was downloaded from the URL
    // below, hashed, and its contents inspected (tools/python.exe, Lib/venv,
    // Lib/ensurepip, Lib/site-packages/pip). A payload whose hash differs is
    // never extracted, let alone executed.
    private const string NuGetPythonUrl = "https://www.nuget.org/api/v2/package/python/3.12.10";
    private const string NuGetPythonSha256 =
        "0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8";
    private const string NuGetPythonVersion = "3.12.10";

    private static readonly HttpClient Http = new();

    /// <summary>One row of the runtime panel.</summary>
    public sealed record StageState(string Name, bool Ready, string Detail);

    /// <summary>Progress callback: (stage name, message).</summary>
    public delegate void SetupProgress(string stage, string message);

    /// <summary>%LocalAppData%\Citadel\runtime, overridable via CITADEL_RUNTIME.</summary>
    public static string RuntimeRoot
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("CITADEL_RUNTIME");
            if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathRooted(overridePath))
            {
                return overridePath;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Citadel",
                "runtime");
        }
    }

    public static string VenvPython
        => Path.Combine(RuntimeRoot, ".venv", "Scripts", "python.exe");

    public static string VendoredPython
        => Path.Combine(RuntimeRoot, "python", "python.exe");

    /// <summary>The deployed read-only payload, beside the shell executable.</summary>
    public static string DeployedPayloadRoot
        => Path.Combine(AppContext.BaseDirectory, "sharedLogic");

    public static string DeployedPyhostScript
        => Path.Combine(DeployedPayloadRoot, "pyhost", "pyhost.py");

    public static string DeployedRequirements
        => Path.Combine(DeployedPayloadRoot, "requirements.txt");

    /// <summary>Resolve the per-feature venv when present, else the shared one.</summary>
    public static string VenvPythonFor(string? feature)
    {
        if (!string.IsNullOrWhiteSpace(feature))
        {
            var own = Path.Combine(RuntimeRoot, "venvs", feature, ".venv", "Scripts", "python.exe");
            if (File.Exists(own))
            {
                return own;
            }
        }

        return VenvPython;
    }

    /// <summary>Read-only status of all four stages; never mutates anything.</summary>
    public static async Task<IReadOnlyList<StageState>> CheckStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var systemPython = await DetectSystemPythonAsync(cancellationToken).ConfigureAwait(false);
        var vendoredReady = systemPython is null
            && await IsVendoredPythonValidAsync(cancellationToken).ConfigureAwait(false);
        var pythonReady = systemPython is not null || vendoredReady;
        var pythonDetail = systemPython is not null
            ? systemPython + " (system)"
            : vendoredReady ? "vendored " + NuGetPythonVersion : "not found";

        var venvReady = File.Exists(VenvPython);
        var packagesReady = false;
        string packagesDetail = "not installed";
        if (venvReady)
        {
            var probe = await RunCaptureAsync(
                VenvPython,
                new[]
                {
                    "-c",
                    "import importlib.metadata as m; "
                    + "print(m.version('camoufox') + '|' + m.version('playwright'))",
                },
                null,
                cancellationToken).ConfigureAwait(false);
            packagesReady = probe.ExitCode == 0
                && Regex.IsMatch(
                    probe.Output,
                    @"^0\.5\.5\|1\.51(?:\.|$)",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant);
            packagesDetail = packagesReady
                ? probe.Output.Split('\n')[0].Replace('|', ' ')
                : "missing or incompatible packages";
        }

        var browserDetail = "not fetched";
        var browserReady = false;
        if (venvReady)
        {
            // Ask camoufox itself which browser version is installed — a bare
            // directory-exists check cannot tell a real binary from debris.
            var probe = await RunCaptureAsync(
                VenvPython,
                new[] { "-c", "from camoufox.pkgman import installed_verstr; print(installed_verstr())" },
                null,
                cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode == 0 && probe.Output.Contains("beta"))
            {
                browserReady = true;
                browserDetail = "camoufox " + probe.Output.Trim();
            }
        }

        return new[]
        {
            new StageState("Python", pythonReady, pythonDetail),
            new StageState("venv", venvReady, venvReady ? VenvPython : "not created"),
            new StageState("packages", packagesReady, packagesDetail),
            new StageState("browser", browserReady, browserDetail),
        };
    }

    /// <summary>
    /// Run the chain. Only one setup may run at a time — across ALL citizens
    /// and processes. This source compiles into every citizen, so a static
    /// semaphore would be per-assembly and useless; the gate is a lock file
    /// at the runtime root (FileShare.None — a second opener gets IOException
    /// even across processes; the OS releases it if the holder dies).
    /// </summary>
    public static async Task<bool> RunAsync(
        SetupProgress? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RuntimeRoot);
        FileStream setupLock;
        try
        {
            setupLock = new FileStream(
                Path.Combine(RuntimeRoot, ".setup.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            progress?.Invoke("setup", "another setup is already running");
            return false;
        }

        try
        {
            var marker = Encoding.UTF8.GetBytes(
                "pid " + Environment.ProcessId + " since " + DateTimeOffset.Now.ToString("o"));
            setupLock.Write(marker);
            setupLock.Flush();

            // 1 — Python.
            var python = await DetectSystemPythonAsync(cancellationToken).ConfigureAwait(false);
            if (python is null)
            {
                python = await EnsureVendoredPythonAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                progress?.Invoke("python", "system " + python);
            }

            // 2 — venv.
            if (!File.Exists(VenvPython))
            {
                progress?.Invoke("venv", "creating " + VenvPython);
                await RunCheckedAsync(
                        python,
                        new[] { "-m", "venv", Path.Combine(RuntimeRoot, ".venv") },
                        null,
                        "venv creation failed",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                progress?.Invoke("venv", "already exists");
            }

            // 3 — packages (re-runnable: pip is idempotent for satisfied pins).
            progress?.Invoke("packages", "pip install -r requirements.txt");
            await RunCheckedAsync(
                    VenvPython,
                    new[]
                    {
                        "-m", "pip", "install", "--disable-pip-version-check",
                        "-r", DeployedRequirements,
                    },
                    null,
                    "pip install failed",
                    cancellationToken)
                .ConfigureAwait(false);

            // 4 — camoufox browser binary (skips itself when the per-machine
            // cache exists, so this is cheap on a machine that already ran it).
            progress?.Invoke("browser", "camoufox fetch");
            await RunCheckedAsync(
                    VenvPython,
                    new[] { "-m", "camoufox", "fetch" },
                    null,
                    "camoufox fetch failed",
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Invoke("setup", "done");
            return true;
        }
        finally
        {
            setupLock.Dispose();
        }
    }

    /// <summary>
    /// First python ≥3.12 that is NOT the Microsoft Store alias. Probes
    /// `py -3`, `python`, `python3`; the store stub is rejected by its
    /// WindowsApps path, and a bare `--version` parse guards the rest.
    /// </summary>
    public static async Task<string?> DetectSystemPythonAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var candidate in new[]
                 {
                     new[] { "py", "-3" },
                     new[] { "python" },
                     new[] { "python3" },
                 })
        {
            var exe = candidate[0];
            var prefix = candidate.Skip(1).ToArray();

            var versionProbe = await RunCaptureAsync(
                    exe,
                    prefix.Append("--version").ToArray(),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (versionProbe.ExitCode != 0)
            {
                continue;
            }

            var match = Regex.Match(versionProbe.Output, @"Python\s+(\d+)\.(\d+)");
            if (!match.Success
                || int.Parse(match.Groups[1].Value) < 3
                || int.Parse(match.Groups[1].Value) == 3 && int.Parse(match.Groups[2].Value) < 12)
            {
                continue;
            }

            var pathProbe = await RunCaptureAsync(
                    exe,
                    prefix.Append("-c").Append("import sys; print(sys.executable)").ToArray(),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            var path = pathProbe.Output.Trim();
            if (pathProbe.ExitCode != 0
                || path.Length == 0
                || path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return path;
        }

        return null;
    }

    private static async Task<string> EnsureVendoredPythonAsync(
        SetupProgress? progress,
        CancellationToken cancellationToken)
    {
        if (await IsVendoredPythonValidAsync(cancellationToken).ConfigureAwait(false))
        {
            progress?.Invoke("python", "vendored runtime already present");
            return VendoredPython;
        }

        var cacheDir = Path.Combine(RuntimeRoot, "cache");
        Directory.CreateDirectory(cacheDir);
        var packagePath = Path.Combine(cacheDir, "python." + NuGetPythonVersion + ".nupkg");
        var partialPath = packagePath + ".partial";

        if (!File.Exists(packagePath))
        {
            progress?.Invoke("python", "downloading CPython " + NuGetPythonVersion + " (nuget)");
            // Download to .partial first: an interrupted download can never
            // masquerade as the real package on the next run.
            var bytes = await Http.GetByteArrayAsync(NuGetPythonUrl, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(partialPath, bytes, cancellationToken)
                .ConfigureAwait(false);

            // The pin is the contract: hash mismatch → delete and refuse.
            var hash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(partialPath, cancellationToken)
                    .ConfigureAwait(false)));
            if (!hash.Equals(NuGetPythonSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partialPath);
                throw new InvalidOperationException(
                    "NuGet python package hash mismatch — refusing to extract. expected "
                    + NuGetPythonSha256 + ", got " + hash);
            }

            File.Move(partialPath, packagePath, overwrite: true);
        }

        // Cached packages are untrusted input too. Verify on every extraction,
        // not only immediately after download: an older interrupted setup,
        // disk corruption, or local replacement must never bypass the pin.
        var cachedHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(packagePath, cancellationToken)
                .ConfigureAwait(false)));
        if (!cachedHash.Equals(NuGetPythonSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(packagePath);
            throw new InvalidOperationException(
                "NuGet python package hash mismatch — refusing to extract. expected "
                + NuGetPythonSha256 + ", got " + cachedHash);
        }

        progress?.Invoke("python", "extracting vendored runtime");
        var target = Path.Combine(RuntimeRoot, "python");
        var staging = Path.Combine(RuntimeRoot, "python.staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith("tools/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var relative = entry.FullName["tools/".Length..];
                    if (relative.Length == 0)
                    {
                        continue;
                    }

                    var destination = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!destination.StartsWith(
                            Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("zip entry escapes target: " + entry.FullName);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            var stagedExe = Path.Combine(staging, "python.exe");
            if (!File.Exists(stagedExe))
            {
                throw new InvalidOperationException(
                    "vendored python missing after extraction: " + stagedExe);
            }

            // Validate the staged interpreter actually runs before it earns
            // the final name.
            var probe = await RunCaptureAsync(
                    stagedExe, new[] { "--version" }, null, cancellationToken)
                .ConfigureAwait(false);
            if (probe.ExitCode != 0 || !probe.Output.Contains("3.12"))
            {
                throw new InvalidOperationException(
                    "staged python failed --version probe: " + Tail(probe.Output));
            }

            // Commit by same-volume directory rename. Preserve an existing
            // runtime as a rollback until the staged runtime owns the final
            // name, so a failed move never destroys the last usable copy.
            string? backup = null;
            if (Directory.Exists(target))
            {
                backup = Path.Combine(
                    RuntimeRoot,
                    "python.backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (backup is not null
                    && Directory.Exists(backup)
                    && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }

            if (backup is not null && Directory.Exists(backup))
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch (Exception ex)
                {
                    // The new runtime is already committed. A locked backup
                    // is harmless and can be removed by a later maintenance
                    // pass; do not report a successful install as failed.
                    progress?.Invoke("python", "old runtime cleanup pending: " + ex.Message);
                }
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        return VendoredPython;
    }

    private static async Task<bool> IsVendoredPythonValidAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(VendoredPython))
        {
            return false;
        }

        var probe = await RunCaptureAsync(
                VendoredPython,
                new[] { "--version" },
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return probe.ExitCode == 0
            && Regex.IsMatch(
                probe.Output,
                @"Python\s+3\.12(?:\.|$)",
                RegexOptions.CultureInvariant);
    }

    private sealed record Captured(int ExitCode, string Output);

    private static async Task<Captured> RunCaptureAsync(
        string exe,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception)
        {
            return new Captured(-1, string.Empty); // not found / not executable
        }

        // stdout and stderr are drained CONCURRENTLY: a chatty child (pip,
        // camoufox fetch) that fills the stderr pipe while we await stdout
        // deadlocks both sides — codex audit H003-review finding #1.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled setup must not leave the child running.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already exited — nothing to kill.
            }

            throw;
        }

        return new Captured(process.ExitCode, (stdoutTask.Result + "\n" + stderrTask.Result).Trim());
    }

    private static async Task RunCheckedAsync(
        string exe,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var result = await RunCaptureAsync(exe, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                failureMessage + " (exit " + result.ExitCode + "): " + Tail(result.Output));
        }
    }

    private static string Tail(string text)
    {
        var normalized = text.Replace("\r", string.Empty);
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var kept = lines.Skip(Math.Max(0, lines.Length - 6));
        return string.Join(" | ", kept);
    }
}
