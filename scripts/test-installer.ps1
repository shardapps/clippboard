param([switch]$RequireSigned, [string]$PreviousInstaller, [string]$PreviousInstallerSHA256)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Installer lifecycle checks run only on an isolated GitHub Actions Windows runner.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $projectRoot
& "$PSScriptRoot\verify-package.ps1" -RequireSigned:$RequireSigned
$info = Get-Content -Raw -LiteralPath 'artifacts\build-info.json' | ConvertFrom-Json
$testDirectory = Join-Path $projectRoot '.data\installer smoke test'
$env:TEMP = Join-Path $projectRoot '.cache\temp'; $env:TMP = $env:TEMP
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $projectRoot '.cache\bundle'
$env:CLIPPBOARD_DEV_DATA = Join-Path $projectRoot '.data\installer-preview'
$logs = Join-Path $projectRoot 'artifacts\installer-tests'
New-Item -ItemType Directory -Force -Path $env:TEMP, $logs | Out-Null
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{52BFACD5-1656-42C4-B488-96B815B572DA}_is1'
if (Test-Path -LiteralPath $registryKey) { throw 'Existing Clippboard installation detected; refusing to overwrite it.' }
if (Test-Path -LiteralPath $testDirectory) { throw 'Installer smoke destination must be fresh.' }
function Invoke-SmokeExecutable([string]$Executable, [string[]]$Arguments) {
  Write-Output "Starting isolated executable: $Executable"
  $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
  try {
    if (!$process.WaitForExit(60000)) { $process.Kill($true); throw "Executable timed out: $Executable" }
    if ($process.ExitCode -ne 0) { throw "Executable failed with exit code $($process.ExitCode): $Executable" }
    Write-Output "Completed isolated executable: $Executable"
  } finally { $process.Dispose() }
}
$setup = Join-Path $projectRoot "artifacts\installer\Clippboard-Setup-$($info.Version).exe"
$uninstall = Join-Path $testDirectory 'unins000.exe'
try {
  if ($PreviousInstaller) {
    $PreviousInstaller = [IO.Path]::GetFullPath($PreviousInstaller)
    if ($PreviousInstallerSHA256 -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $PreviousInstaller).Hash -ne $PreviousInstallerSHA256) { throw 'Unexpected upgrade baseline installer.' }
    Invoke-SmokeExecutable $PreviousInstaller @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$testDirectory`"", "/LOG=`"$logs\upgrade-install.log`"")
    & dotnet tests/Clippboard.Core.Tests/bin/Release/net10.0-windows10.0.22000.0/Clippboard.Core.Tests.dll --upgrade-fixture (Join-Path $testDirectory 'Clippboard.exe') (Join-Path $logs 'upgrade-data') create
    if ($LASTEXITCODE -ne 0) { throw 'Previous version fixture failed.' }
  }
  Invoke-SmokeExecutable $setup @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$testDirectory`"", "/LOG=`"$logs\install.log`"")
  $installed = Join-Path $testDirectory 'Clippboard.exe'
  if (!(Test-Path -LiteralPath $installed) -or !(Test-Path -LiteralPath $uninstall) -or !(Test-Path -LiteralPath $registryKey)) { throw 'Installation did not create expected files and uninstall registration.' }
  if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath 'artifacts\portable\win-x64\Clippboard.exe').Hash) { throw 'Installed binary differs from packaged binary.' }
  if ($PreviousInstaller) {
    & dotnet tests/Clippboard.Core.Tests/bin/Release/net10.0-windows10.0.22000.0/Clippboard.Core.Tests.dll --upgrade-fixture $installed (Join-Path $logs 'upgrade-data') verify
    if ($LASTEXITCODE -ne 0) { throw 'Upgraded version did not preserve fixture history/settings.' }
  }
  if ($RequireSigned -or $info.Signed) { if ((Get-AuthenticodeSignature -LiteralPath $uninstall).Status -ne 'Valid') { throw 'Embedded uninstaller is not validly signed.' } }
  # Same-version repair follows the optional cross-version fixture above.
  Invoke-SmokeExecutable $setup @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$testDirectory`"", "/LOG=`"$logs\reinstall.log`"")
  $preview = Join-Path $logs 'installed-preview.png'
  Invoke-SmokeExecutable $installed @('--preview', "`"$preview`"", '--theme', 'Ocean')
  if (!(Test-Path -LiteralPath $preview)) { throw 'Installed app did not render its synthetic preview.' }
} finally {
  if (Test-Path -LiteralPath $uninstall) { Invoke-SmokeExecutable $uninstall @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$logs\uninstall.log`"") }
}
if ((Test-Path -LiteralPath (Join-Path $testDirectory 'Clippboard.exe')) -or (Test-Path -LiteralPath $registryKey)) { throw 'Uninstall did not remove the binary and registration.' }
Write-Output 'PASS: isolated installation, optional cross-version preservation, same-version reinstall, installed native preview, and uninstall.'
