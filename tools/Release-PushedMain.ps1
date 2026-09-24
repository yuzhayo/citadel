param(
    [ValidateSet('', 'patch', 'minor', 'major')]
    [string] $Bump = 'patch'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Bump)) {
    $Bump = 'patch'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$versionFile = Join-Path $repositoryRoot 'version.props'
Set-Location -LiteralPath $repositoryRoot

function Invoke-Native([string] $FileName, [string[]] $Arguments) {
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName gagal dengan exit code $LASTEXITCODE."
    }
}

function Wait-ForWorkflowRun(
    [string] $Workflow,
    [string] $Commit,
    [string] $Event,
    [DateTime] $NotBeforeUtc) {
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    do {
        $json = gh run list `
            --workflow $Workflow `
            --branch main `
            --commit $Commit `
            --event $Event `
            --limit 10 `
            --json databaseId,createdAt,headSha
        if ($LASTEXITCODE -ne 0) {
            throw "Tidak dapat membaca workflow $Workflow."
        }

        $run = @($json | ConvertFrom-Json) |
            Where-Object {
                $_.headSha -eq $Commit -and
                ([DateTime]$_.createdAt).ToUniversalTime() -ge $NotBeforeUtc.AddSeconds(-5)
            } |
            Sort-Object { [DateTime]$_.createdAt } -Descending |
            Select-Object -First 1
        if ($run) {
            return $run
        }

        Start-Sleep -Seconds 3
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Workflow $Workflow untuk commit $Commit tidak muncul dalam 2 menit."
}

function Get-NextVersion([string] $Kind) {
    $projectVersion = [Version]::Parse((& (Join-Path $PSScriptRoot 'Get-ProjectVersion.ps1')).Trim())
    $tagVersions = @(git tag --list 'v*' | ForEach-Object {
        $parsed = [Version]::new()
        if ([Version]::TryParse($_.TrimStart('v'), [ref] $parsed) -and $parsed.Revision -le 0) {
            $parsed
        }
    })
    $base = @($projectVersion) + $tagVersions | Sort-Object -Descending | Select-Object -First 1

    $next = switch ($Kind) {
        'patch' { [Version]::new($base.Major, $base.Minor, $base.Build + 1) }
        'minor' { [Version]::new($base.Major, $base.Minor + 1, 0) }
        'major' { [Version]::new($base.Major + 1, 0, 0) }
    }
    $next.ToString(3)
}

try {
    if ((git branch --show-current) -ne 'main') {
        throw 'Release hanya dapat dijalankan dari branch main.'
    }

    Invoke-Native gh @('auth', 'status')
    Invoke-Native git @('fetch', 'origin', 'main', '--tags')

    & git diff --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Ada perubahan tracked yang belum di-commit. Commit atau stash dahulu.'
    }
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Ada perubahan staged yang belum di-commit. Commit atau unstage dahulu.'
    }

    $headBeforeBump = (git rev-parse HEAD).Trim()
    $remoteHead = (git rev-parse origin/main).Trim()
    if ($headBeforeBump -ne $remoteHead) {
        throw "HEAD lokal ($headBeforeBump) tidak sama dengan origin/main ($remoteHead). Sinkronkan dahulu."
    }

    $releaseVersion = Get-NextVersion $Bump
    $content = [System.IO.File]::ReadAllText($versionFile)
    $updated = [regex]::Replace(
        $content,
        '<CitadelVersion>\d+\.\d+\.\d+</CitadelVersion>',
        "<CitadelVersion>$releaseVersion</CitadelVersion>",
        1)
    if ($updated -eq $content) {
        throw 'CitadelVersion tiga bagian tidak ditemukan di version.props.'
    }
    [System.IO.File]::WriteAllText($versionFile, $updated, [System.Text.UTF8Encoding]::new($false))

    Invoke-Native git @('add', '--', 'version.props')
    Invoke-Native git @('commit', '-m', "chore(release): bump version to $releaseVersion")
    $commit = (git rev-parse HEAD).Trim()
    $ciNotBeforeUtc = [DateTime]::UtcNow
    Invoke-Native git @('push', 'origin', 'main')

    Write-Host "[Citadel] Menunggu CI untuk $commit..."
    $ciRun = Wait-ForWorkflowRun 'ci.yml' $commit 'push' $ciNotBeforeUtc
    Invoke-Native gh @('run', 'watch', [string]$ciRun.databaseId, '--exit-status')

    Write-Host "[Citadel] Menjalankan release v$releaseVersion..."
    $releaseNotBeforeUtc = [DateTime]::UtcNow
    Invoke-Native gh @('workflow', 'run', 'release.yml', '--ref', 'main', '-f', "bump=$Bump")
    $releaseRun = Wait-ForWorkflowRun 'release.yml' $commit 'workflow_dispatch' $releaseNotBeforeUtc
    Invoke-Native gh @('run', 'watch', [string]$releaseRun.databaseId, '--exit-status')

    $release = gh release view "v$releaseVersion" --json tagName,targetCommitish,url | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $release.tagName -ne "v$releaseVersion" -or $release.targetCommitish -ne $commit) {
        throw "Release v$releaseVersion untuk commit $commit tidak dapat diverifikasi."
    }

    Write-Host "[Citadel] Commit: $commit"
    Write-Host "[Citadel] Release: $($release.url)"
}
catch {
    Write-Error $_
    exit 1
}
