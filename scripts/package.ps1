param([string]$CompilerPath, [switch]$SkipPublish,
  [ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint,
  [string]$SignToolPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:TEMP = Join-Path $projectRoot '.cache\temp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
if ($SignToolPath -and !$CertificateThumbprint) { throw 'A certificate thumbprint is required for a signed build.' }
if ($CertificateThumbprint -and !$SignToolPath) { throw 'Pass -SignToolPath from the Windows SDK for a signed build.' }
if (!$SkipPublish) { & "$PSScriptRoot\dev.ps1" publish }
if (!$CompilerPath) {
  $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($compiler) { $CompilerPath = $compiler.Source }
  else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 6 or pass -CompilerPath to its ISCC.exe.' }
$buildProperties = [xml][IO.File]::ReadAllText((Join-Path $projectRoot 'Directory.Build.props'))
$appVersion = $buildProperties.Project.PropertyGroup.Version
$compilerArgs = @("/DAppVersion=$appVersion")
if ($CertificateThumbprint) {
  & "$PSScriptRoot\sign.ps1" -Path 'artifacts\portable\win-x64\Clippboard.exe' -CertificateThumbprint $CertificateThumbprint -SignToolPath $SignToolPath
  # Inno signs both setup and its embedded uninstaller, never just the outer EXE.
  $signScript = (Join-Path $PSScriptRoot 'sign.ps1').Replace('$', '$$')
  $signExecutable = (Resolve-Path -LiteralPath $SignToolPath).ProviderPath.Replace('$', '$$')
  $signCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File $q' + $signScript + '$q -CertificateThumbprint ' + $CertificateThumbprint + ' -SignToolPath $q' + $signExecutable + '$q -Path $f'
  $compilerArgs += @('/DSignedBuild', '/Sshardapps=' + $signCommand)
}
& $CompilerPath @compilerArgs 'installer\Clippboard.iss'
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$artifactPaths = @('artifacts\portable\win-x64\Clippboard.exe', "artifacts\installer\Clippboard-Setup-$appVersion.exe")
$checksums = foreach ($path in $artifactPaths) {
  $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
  "$(($hash.Hash).ToLowerInvariant())  $($path.Replace('\','/'))"
}
$checksums | Set-Content -LiteralPath 'artifacts\SHA256SUMS.txt' -Encoding utf8
$signatures = foreach ($path in $artifactPaths) {
  $signature = Get-AuthenticodeSignature -LiteralPath $path
  if ($CertificateThumbprint -and ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint)) { throw "Invalid signed artifact: $path" }
  [ordered]@{ Path = $path.Replace('\','/'); Status = $signature.Status.ToString(); Publisher = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null } }
}
[ordered]@{ Version = $appVersion; Signed = [bool]$CertificateThumbprint; Artifacts = @($signatures) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath 'artifacts\build-info.json' -Encoding utf8
