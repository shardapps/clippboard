param([switch]$RequireSigned)
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
  $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
  if (!$process.WaitForExit(60000)) { $process.Kill(); throw "Executable timed out: $Executable" }
  if ($process.ExitCode -ne 0) { throw "Executable failed with exit code $($process.ExitCode): $Executable" }
}
$setup = Join-Path $projectRoot "artifacts\installer\Clippboard-Setup-$($info.Version).exe"
$uninstall = Join-Path $testDirectory 'unins000.exe'
try {
  Invoke-SmokeExecutable $setup @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$testDirectory`"", "/LOG=`"$logs\install.log`"")
  $installed = Join-Path $testDirectory 'Clippboard.exe'
  if (!(Test-Path -LiteralPath $installed) -or !(Test-Path -LiteralPath $uninstall) -or !(Test-Path -LiteralPath $registryKey)) { throw 'Installation did not create expected files and uninstall registration.' }
  if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath 'artifacts\portable\win-x64\Clippboard.exe').Hash) { throw 'Installed binary differs from packaged binary.' }
  if ($RequireSigned -or $info.Signed) { if ((Get-AuthenticodeSignature -LiteralPath $uninstall).Status -ne 'Valid') { throw 'Embedded uninstaller is not validly signed.' } }
  # Reinstall the same version tests repair/overwrite behavior; cross-version upgrade remains separate validation.
  Invoke-SmokeExecutable $setup @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$testDirectory`"", "/LOG=`"$logs\reinstall.log`"")
  $preview = Join-Path $logs 'installed-preview.png'
  Invoke-SmokeExecutable $installed @('--preview', "`"$preview`"", '--theme', 'Ocean')
  if (!(Test-Path -LiteralPath $preview)) { throw 'Installed app did not render its synthetic preview.' }
} finally {
  if (Test-Path -LiteralPath $uninstall) { Invoke-SmokeExecutable $uninstall @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$logs\uninstall.log`"") }
}
if ((Test-Path -LiteralPath (Join-Path $testDirectory 'Clippboard.exe')) -or (Test-Path -LiteralPath $registryKey)) { throw 'Uninstall did not remove the binary and registration.' }
Write-Output 'PASS: isolated installation, same-version reinstall, installed native preview, and uninstall.'
