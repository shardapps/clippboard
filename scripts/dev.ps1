param([ValidateSet('build','test','run','publish','preview','diagnostics','lifecycle','decode','metadata','format')][string]$Task = 'build',
  [ValidateSet('Cloud','Paper','Midnight','Lavender','Ocean','Rose')][string]$Theme = 'Cloud',
  [switch]$Settings, [switch]$Help, [switch]$Notices, [switch]$Detail, [switch]$Snippet, [switch]$Combine, [switch]$Ocr, [switch]$Backup, [string]$PreviewPath = 'artifacts/preview.png', [switch]$Measure, [switch]$Software, [switch]$ScrollOnly, [switch]$BuildDiagnostics, [switch]$RuntimeOnlyPreparation)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.cache\nuget'
# NuGet's mmap extraction can fail on streamed/virtual drives. Ordinary file
# extraction keeps package signature verification and works inside this workspace.
if (!$env:NUGET_PACKAGE_EXTRACTION_USE_MMAP) { $env:NUGET_PACKAGE_EXTRACTION_USE_MMAP = '0' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:CLIPPBOARD_DEV_DATA = Join-Path $projectRoot '.data'
$env:TEMP = Join-Path $projectRoot '.cache\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
switch ($Task) {
  'metadata' { & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- --metadata-diagnostics (Join-Path $projectRoot 'artifacts/metadata-diagnostics.json') }
  'decode' { & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- --decode-diagnostics (Join-Path $projectRoot 'artifacts/decode-diagnostics.json') }
  'lifecycle' {
    $diagnosticArgs = @('--lifecycle-diagnostics', (Join-Path $projectRoot 'artifacts/overlay-lifecycle.json'))
    if ($Software) { $diagnosticArgs += '--software' }
    if ($RuntimeOnlyPreparation) { $diagnosticArgs += '--runtime-only-preparation' }
    & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- @diagnosticArgs
  }
  'diagnostics' {
    $diagnosticArgs = @('--diagnostics', (Join-Path $projectRoot 'artifacts/motion-diagnostics.json'))
    if ($ScrollOnly) { $diagnosticArgs += '--scroll-only' }
    if ($Software) { $diagnosticArgs += '--software' }
    if ($RuntimeOnlyPreparation) { $diagnosticArgs += '--runtime-only-preparation' }
    & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- @diagnosticArgs
  }
  'build' { $verbosity = if ($BuildDiagnostics) { 'diagnostic' } else { 'minimal' }; & $dotnetPath build Clippboard.slnx -c Release -m:1 -p:UseAppHost=false -verbosity:$verbosity }
  'test' { & $dotnetPath run --project tests/Clippboard.Core.Tests -c Release -p:UseAppHost=false }
  'run' { & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- --dev }
  'format' { & $dotnetPath format Clippboard.slnx --verify-no-changes }
  'publish' { & $dotnetPath publish src/Clippboard.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:ClippboardPublishProfile=bundle -p:RestoreLockedMode=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/portable/win-x64 }
  'preview' {
    $previewArgs = @('--preview', (Join-Path $projectRoot $PreviewPath), '--theme', $Theme)
    if ($Settings) { $previewArgs += '--settings' }
    if ($Help) { $previewArgs += '--help-preview' }
    if ($Notices) { $previewArgs += '--notices' }
    if ($Detail) { $previewArgs += '--detail' }
    if ($Snippet) { $previewArgs += '--snippet' }
    if ($Combine) { $previewArgs += '--combination' }
    if ($Ocr) { $previewArgs += '--ocr' }
    if ($Backup) { $previewArgs += '--backup' }
    if ($Measure) { $previewArgs += @('--measure', (Join-Path $projectRoot 'artifacts/performance.json')) }
    & $dotnetPath run --project src/Clippboard.App -c Release -p:UseAppHost=false -- @previewArgs
  }
}
if ($LASTEXITCODE -ne 0) { throw "dotnet $Task failed with exit code $LASTEXITCODE" }
