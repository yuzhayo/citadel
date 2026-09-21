param(
    [string] $Message,
    [ValidateSet('patch', 'minor', 'major')]
    [string] $Bump = 'patch'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
            --json databaseId,createdAt,headSha,status,conclusion
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

try {
    if ((git branch --show-current) -ne 'main') {
        throw 'Commit dan release hanya boleh dijalankan dari branch main.'
    }
    Invoke-Native git @('remote', 'get-url', 'origin')
    Invoke-Native gh @('auth', 'status')
    Invoke-Native git @('fetch', 'origin', 'main')

    & git merge-base --is-ancestor origin/main HEAD
    if ($LASTEXITCODE -ne 0) {
        throw 'Branch main lokal tertinggal atau divergen dari origin/main. Sinkronkan terlebih dahulu.'
    }

    $changes = @(git status --short)
    if ($LASTEXITCODE -ne 0) {
        throw 'Tidak dapat membaca git status.'
    }
    if ($changes.Count -eq 0) {
        throw 'Tidak ada perubahan untuk di-commit.'
    }

    Write-Host ''
    Write-Host '[Citadel] Perubahan yang akan di-commit:'
    $changes | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    if ((Read-Host 'Stage semua perubahan di atas? Ketik YES untuk lanjut') -ne 'YES') {
        throw 'Dibatalkan; tidak ada file yang di-stage.'
    }

    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = Read-Host 'Commit message'
    }
    if ([string]::IsNullOrWhiteSpace($Message)) {
        throw 'Commit message tidak boleh kosong.'
    }

    Write-Host '[Citadel] Menjalankan preflight CI lokal sebelum staging...'
    Invoke-Native dotnet @('restore', 'Citadel.slnx')
    Invoke-Native dotnet @(
        'test', 'Citadel.slnx',
        '--configuration', 'Release',
        '--no-restore',
        '--nologo')
    foreach ($citizen in @('ftf', 'proxy', 'blank', 'camoprof', 'mangareader')) {
        $projectName = switch ($citizen) {
            'ftf' { 'Module.FTF.csproj' }
            'proxy' { 'Module.Proxy.csproj' }
            'blank' { 'Module.Blank.csproj' }
            'camoprof' { 'Module.Camoprof.csproj' }
            'mangareader' { 'Module.Mangareader.csproj' }
        }
        Invoke-Native dotnet @(
            'build', "module/$citizen/$projectName",
            '-v', 'q',
            '--nologo')
    }

    Invoke-Native git @('add', '-A', '--', '.')
    & git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        throw 'Tidak ada perubahan staged untuk di-commit.'
    }
    if ($LASTEXITCODE -ne 1) {
        throw 'Tidak dapat memeriksa staged diff.'
    }

    Invoke-Native git @('commit', '-m', $Message.Trim())
    $commit = (git rev-parse HEAD).Trim()
    $ciNotBefore = [DateTime]::UtcNow
    Invoke-Native git @('push', 'origin', 'main')

    Write-Host "[Citadel] Menunggu CI untuk $commit..."
    $ciRun = Wait-ForWorkflowRun 'ci.yml' $commit 'push' $ciNotBefore
    Invoke-Native gh @('run', 'watch', [string]$ciRun.databaseId, '--exit-status')

    $releaseVersion = (& (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -Bump $Bump).Trim()
    if ($LASTEXITCODE -ne 0 -or $releaseVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Tidak dapat menentukan version release.'
    }

    Write-Host "[Citadel] Menjalankan release v$releaseVersion..."
    $releaseNotBefore = [DateTime]::UtcNow
    Invoke-Native gh @('workflow', 'run', 'release.yml', '--ref', 'main', '-f', "bump=$Bump")
    $releaseRun = Wait-ForWorkflowRun 'release.yml' $commit 'workflow_dispatch' $releaseNotBefore
    Invoke-Native gh @('run', 'watch', [string]$releaseRun.databaseId, '--exit-status')

    $releaseJson = gh release view "v$releaseVersion" --json tagName,targetCommitish,url
    if ($LASTEXITCODE -ne 0) {
        throw "Workflow selesai tetapi GitHub release v$releaseVersion tidak ditemukan."
    }
    $release = $releaseJson | ConvertFrom-Json
    if ($release.tagName -ne "v$releaseVersion") {
        throw "Tag release tidak sesuai: $($release.tagName)."
    }

    Write-Host "[Citadel] Commit: $commit"
    Write-Host "[Citadel] Release: $($release.url)"
}
catch {
    Write-Error $_
    exit 1
}
