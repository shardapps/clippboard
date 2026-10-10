param([ValidateSet('build','test','run','publish','preview','format')][string]$Task = 'build')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.cache\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:CLIPPBOARD_DEV_DATA = Join-Path $projectRoot '.data'
$env:TEMP = Join-Path $projectRoot '.cache\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
switch ($Task) {
  'build' { & $dotnetPath build Clippboard.slnx -c Release -m:1 -p:UseAppHost=false }
  'test' { & $dotnetPath run --project tests/Clippboard.Core.Tests -c Release -p:UseAppHost=false }
  'run' { & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- --dev }
  'format' { & $dotnetPath format Clippboard.slnx --verify-no-changes }
  'publish' { & $dotnetPath publish src/Clippboard.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/portable/win-x64 }
  'preview' { & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- --preview (Join-Path $projectRoot 'artifacts\preview.png') }
}
if ($LASTEXITCODE -ne 0) { throw "dotnet $Task failed with exit code $LASTEXITCODE" }
