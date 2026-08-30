param(
    [string] $Version = (& (Join-Path $PSScriptRoot 'Get-ProjectVersion.ps1')),
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',
    [string] $PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\Releases'),
    [string] $ReleaseNotes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version harus menggunakan semver tiga bagian, contoh 0.1.0.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'

function Resolve-RepositoryOutput([string] $Path) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if ($resolved -eq $repositoryRoot -or
        -not $resolved.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output harus berupa subfolder repository: $resolved"
    }
    $resolved
}

$publishPath = Resolve-RepositoryOutput $PublishDirectory
$outputPath = Resolve-RepositoryOutput $OutputDirectory

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$shellProject = Join-Path $repositoryRoot 'core\Citadel.Shell\Citadel.Shell.csproj'
$publishArguments = @(
    'publish', $shellProject,
    '--configuration', 'Release',
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '--output', $publishPath,
    '--nologo',
    "-p:CitadelVersion=$Version",
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# A copied module folder must ship automatically without adding its project to the
# solution or editing this script. The established Module.*.csproj naming rule is
# therefore also the release-discovery rule.
$moduleSourceRoot = Join-Path $repositoryRoot 'module'
$citizenProjects = @(
    Get-ChildItem -LiteralPath $moduleSourceRoot -Directory |
        ForEach-Object {
            Get-ChildItem -LiteralPath $_.FullName -Filter 'Module.*.csproj' -File
        } |
        Sort-Object FullName
)
if ($citizenProjects.Count -eq 0) {
    throw 'Tidak ada citizen Module.*.csproj yang ditemukan untuk dipaketkan.'
}

$citizenRuntimeRoot = (Join-Path $publishPath 'module') + '\'
foreach ($project in $citizenProjects) {
    & dotnet build $project.FullName `
        --configuration Release `
        --nologo `
        "-p:CitadelVersion=$Version" `
        "-p:CitizenRuntimeRoot=$citizenRuntimeRoot" `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$mainExe = Join-Path $publishPath 'Citadel.Shell.exe'
if (-not (Test-Path -LiteralPath $mainExe -PathType Leaf)) {
    throw "Executable publish tidak ditemukan: $mainExe"
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'Components') -PathType Container)) {
    throw 'Folder Components tidak ikut publish.'
}

$deployedModules = @(Get-ChildItem -LiteralPath $citizenRuntimeRoot -Directory)
if ($deployedModules.Count -ne $citizenProjects.Count) {
    throw "Jumlah citizen source ($($citizenProjects.Count)) dan deployment ($($deployedModules.Count)) berbeda."
}

$sharedAssemblyNames = @(
    'Citadel.Core.dll',
    'Citadel.Contract.dll',
    'Citadel.Setting.dll',
    'Citadel.Ui.dll',
    'Citadel.Shell.dll'
)
$contamination = @(
    Get-ChildItem -LiteralPath $citizenRuntimeRoot -Recurse -File |
        Where-Object Name -In $sharedAssemblyNames
)
if ($contamination.Count -gt 0) {
    throw "Citizen deployment membawa shared Citadel DLL: $($contamination.FullName -join ', ')"
}

& dotnet tool restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$packArguments = @(
    'vpk', 'pack',
    '--packId', 'Yuzhayo.Citadel',
    '--packVersion', $Version,
    '--packDir', $publishPath,
    '--mainExe', 'Citadel.Shell.exe',
    '--packTitle', 'Citadel',
    '--packAuthors', 'yuzhayo',
    '--icon', (Join-Path $repositoryRoot 'core\Citadel.Shell\Assets\Citadel.ico'),
    '--outputDir', $outputPath,
    '--runtime', $Runtime,
    '--shortcuts', 'Desktop,StartMenuRoot'
)
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $notesPath = [System.IO.Path]::GetFullPath($ReleaseNotes)
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "Release notes tidak ditemukan: $notesPath"
    }
    $packArguments += @('--releaseNotes', $notesPath)
}

& dotnet @packArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = Get-ChildItem -LiteralPath $outputPath -Filter '*-Setup.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $setup) {
    throw 'Velopack tidak menghasilkan installer Setup.exe.'
}

[pscustomobject]@{
    Version = $Version
    Setup = $setup.FullName
    Sha256 = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash
    Modules = @($deployedModules.Name)
} | ConvertTo-Json
