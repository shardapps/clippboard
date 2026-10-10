param([string]$CompilerPath, [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
if (!$SkipPublish) { & "$PSScriptRoot\dev.ps1" publish }
if (!$CompilerPath) {
  $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($compiler) { $CompilerPath = $compiler.Source }
  else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 6 or pass -CompilerPath to its ISCC.exe.' }
& $CompilerPath '/DAppVersion=0.1.0' 'installer\Clippboard.iss'
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$artifactPaths = @('artifacts\portable\win-x64\Clippboard.exe', 'artifacts\installer\Clippboard-Setup-0.1.0.exe')
$checksums = foreach ($path in $artifactPaths) {
  $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
  "$(($hash.Hash).ToLowerInvariant())  $($path.Replace('\','/'))"
}
$checksums | Set-Content -LiteralPath 'artifacts\SHA256SUMS.txt' -Encoding utf8
