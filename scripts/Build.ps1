param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactDir = Join-Path $projectRoot 'artifacts'
$tempDir = Join-Path $artifactDir 'temp'
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$priorTemp = $env:TEMP
$priorTmp = $env:TMP
try {
    $env:TEMP = $tempDir
    $env:TMP = $tempDir
    if (-not $SkipTests) {
        dotnet test (Join-Path $projectRoot 'tests/AsheTrader.Core.Tests/AsheTrader.Core.Tests.csproj') -c Release -p:UseSharedCompilation=false --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    dotnet build (Join-Path $projectRoot 'src/AsheTrader/AsheTrader.csproj') -c Release -p:TargetPlatformMoniker=Windows%2cVersion=7.0 -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    $manifestPath = Join-Path $projectRoot 'src/AsheTrader/bin/Release/AsheTrader.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $package = Join-Path $artifactDir ("AsheTrader-{0}.zip" -f $manifest.AssemblyVersion)
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src/AsheTrader/bin/Release/AsheTrader/latest.zip') -Destination $package -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $artifactDir 'AsheTrader.json') -Force
    Write-Output $package
} finally {
    $env:TEMP = $priorTemp
    $env:TMP = $priorTmp
}
