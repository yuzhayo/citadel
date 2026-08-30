param(
    [string] $Project = (Join-Path $PSScriptRoot '..\version.props')
)

$ErrorActionPreference = 'Stop'
$content = Get-Content -LiteralPath $Project -Raw
$match = [regex]::Match(
    $content,
    '<CitadelVersion>(?<version>\d+\.\d+\.\d+)</CitadelVersion>')
if (-not $match.Success) {
    throw "CitadelVersion semver tiga bagian tidak ditemukan di $Project."
}

$match.Groups['version'].Value
