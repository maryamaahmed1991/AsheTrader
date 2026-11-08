param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$GitHubRepository,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ReleaseTag,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestFile = Join-Path $projectRoot 'artifacts/AsheTrader.json'
if (-not (Test-Path -LiteralPath $manifestFile)) { throw 'Run scripts/Build.ps1 first.' }
$entry = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json -AsHashtable
$asset = "AsheTrader-$($entry.AssemblyVersion).zip"
$releaseUrl = "https://github.com/$GitHubRepository/releases/download/$ReleaseTag/$asset"
$entry['RepoUrl'] = "https://github.com/$GitHubRepository"
$entry['DownloadLinkInstall'] = $releaseUrl
$entry['DownloadLinkUpdate'] = $releaseUrl
$entry['LastUpdate'] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
if (-not $OutputPath) { $OutputPath = Join-Path $projectRoot 'repo.json' }
$parentDir = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
ConvertTo-Json -InputObject @($entry) -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output $OutputPath
