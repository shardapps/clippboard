$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
  throw 'Installed runtime tests require a disposable Windows GitHub Actions runner.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$version = (Get-ClippboardBuildVersion $projectRoot).Source
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs/Clippboard'
if (Test-Path -LiteralPath $installRoot) { throw 'Refusing to modify an existing installation.' }
$installer = Join-Path $projectRoot "artifacts/installer/Clippboard-Setup-$version.exe"
$installed = Join-Path $installRoot 'Clippboard.exe'
$expectedHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'artifacts/portable/win-x64/Clippboard.exe')).Hash
$owned = $false
function Invoke-CheckedProcess([string]$File, [string[]]$Arguments, [int]$DeadlineSeconds) {
  $start = [Diagnostics.ProcessStartInfo]::new($File)
  $start.UseShellExecute = $false; $start.CreateNoWindow = $true
  foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
  $process = [Diagnostics.Process]::Start($start)
  try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!$process.WaitForExit(1000)) {
      if ($watch.Elapsed.TotalSeconds -gt $DeadlineSeconds) { $process.Kill($true); throw 'Owned installed-test process exceeded its deadline.' }
    }
    if ($process.ExitCode -ne 0) { throw "Installed-test process failed: exit $($process.ExitCode)" }
  } finally { $process.Dispose() }
}
try {
  Invoke-CheckedProcess $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/SP-') 60
  if (!(Test-Path -LiteralPath $installed)) { throw 'Installer did not create its normal per-user executable.' }
  $owned = $true
  if ((Get-FileHash -LiteralPath $installed).Hash -ne $expectedHash) { throw 'Installed binary differs from the exact validated portable input.' }
  Write-Output 'Testing the installed self-contained executable at its default per-user location, using synthetic isolated data.'
  & dotnet 'tests/Clippboard.Core.Tests/bin/Release/net10.0-windows10.0.22000.0/Clippboard.Core.Tests.dll' --session $installed 'artifacts/session-checks/report.json'
  if ($LASTEXITCODE -ne 0) { throw 'Installed native listener/shortcut/paste checks failed.' }
  Invoke-CheckedProcess $installed @('--lifecycle-diagnostics', (Join-Path $projectRoot 'artifacts/overlay-lifecycle-installed.json')) 150
  if (!(Test-Path -LiteralPath 'artifacts/overlay-lifecycle-installed.json')) { throw 'Installed runtime did not produce its lifecycle report.' }
} finally {
  if ($owned) {
    $uninstaller = Join-Path $installRoot 'unins000.exe'
    if (!(Test-Path -LiteralPath $uninstaller)) { throw 'Owned test installation has no uninstaller.' }
    Invoke-CheckedProcess $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') 60
  }
}
