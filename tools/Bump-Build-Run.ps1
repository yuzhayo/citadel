$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$versionFile = Join-Path $repositoryRoot 'version.props'
$releaseDirectory = Join-Path $repositoryRoot 'Releases'
$buildScript = Join-Path $PSScriptRoot 'Build-Release.ps1'
$publishExe = Join-Path $repositoryRoot 'artifacts\publish\win-x64\Citadel.Shell.exe'

try {
    Get-Process 'Citadel.Shell' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    $content = [System.IO.File]::ReadAllText($versionFile)
    $match = [regex]::Match(
        $content,
        '<CitadelVersion>(\d+)\.(\d+)\.(\d+)</CitadelVersion>')
    if (-not $match.Success) {
        throw 'CitadelVersion tiga bagian tidak ditemukan di version.props.'
    }

    $highestVersion = [version]::new(
        [int]$match.Groups[1].Value,
        [int]$match.Groups[2].Value,
        [int]$match.Groups[3].Value)

    if (Test-Path -LiteralPath $releaseDirectory -PathType Container) {
        foreach ($package in Get-ChildItem -LiteralPath $releaseDirectory -File -Filter '*-full.nupkg') {
            if ($package.Name -notmatch '^Yuzhayo\.Citadel-(\d+\.\d+\.\d+)-full\.nupkg$') {
                continue
            }

            $packageVersion = [version]$Matches[1]
            if ($packageVersion -gt $highestVersion) {
                $highestVersion = $packageVersion
            }
        }
    }

    $nextVersion = [version]::new(
        $highestVersion.Major,
        $highestVersion.Minor,
        $highestVersion.Build + 1).ToString(3)
    $updated = $content.Remove($match.Index, $match.Length).Insert(
        $match.Index,
        '<CitadelVersion>' + $nextVersion + '</CitadelVersion>')

    Write-Host "[Citadel] Building release $nextVersion..."
    $buildStartedUtc = [DateTime]::UtcNow

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $startInfo.UseShellExecute = $false
    foreach ($argument in @(
        '-NoLogo',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $buildScript,
        '-Version', $nextVersion)) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $buildProcess = [System.Diagnostics.Process]::Start($startInfo)
    $buildProcess.WaitForExit()
    if ($buildProcess.ExitCode -ne 0) {
        throw "Build release gagal dengan exit code $($buildProcess.ExitCode)."
    }

    $fullPackage = Join-Path $releaseDirectory "Yuzhayo.Citadel-$nextVersion-full.nupkg"
    $setup = Join-Path $releaseDirectory 'Yuzhayo.Citadel-win-Setup.exe'
    foreach ($artifact in @($fullPackage, $setup, $publishExe)) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
            throw "Artefak build tidak ditemukan: $artifact"
        }
        if ((Get-Item -LiteralPath $artifact).LastWriteTimeUtc -lt $buildStartedUtc.AddSeconds(-1)) {
            throw "Artefak build tidak diperbarui untuk $nextVersion`: $artifact"
        }
    }

    $productVersion = (Get-Item -LiteralPath $publishExe).VersionInfo.ProductVersion
    if (-not $productVersion.StartsWith($nextVersion, [StringComparison]::Ordinal)) {
        throw "Versi executable '$productVersion' tidak sesuai target '$nextVersion'."
    }

    [System.IO.File]::WriteAllText(
        $versionFile,
        $updated,
        [System.Text.UTF8Encoding]::new($false))

    $application = Start-Process -FilePath $publishExe -PassThru
    Write-Host "[Citadel] Version saved: $nextVersion"
    Write-Host "[Citadel] Running PID $($application.Id): $publishExe"
}
catch {
    Write-Error $_
    exit 1
}
