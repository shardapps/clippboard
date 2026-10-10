$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$fixture = Join-Path $projectRoot '.cache\packaging-guard-fixture'
New-Item -ItemType Directory -Force -Path "$fixture\scripts", "$fixture\artifacts" | Out-Null
function Expect-Rejection([scriptblock]$Action, [string]$Reason) {
  $rejected = $false
  try { & $Action } catch { if ($_.Exception.Message -notlike "*$Reason*") { throw }; $rejected = $true }
  if (!$rejected) { throw "Expected rejection: $Reason" }
}
try {
  Expect-Rejection { & "$PSScriptRoot\sign.ps1" -Path (Join-Path $env:WINDIR 'System32\cmd.exe') -CertificateThumbprint ('0' * 40) -SignToolPath 'missing' } 'Only project build files'
  Expect-Rejection { & "$PSScriptRoot\package.ps1" -SkipPublish -SignToolPath 'missing' } 'thumbprint is required'
  Expect-Rejection { & "$PSScriptRoot\package.ps1" -SkipPublish -CertificateThumbprint ('0' * 40) } 'Pass -SignToolPath'
  Copy-Item -LiteralPath "$PSScriptRoot\verify-package.ps1" -Destination "$fixture\scripts\verify-package.ps1"
  '{"Version":"0.3.0","Signed":false,"Artifacts":[]}' | Set-Content -LiteralPath "$fixture\artifacts\build-info.json" -Encoding utf8
  Expect-Rejection { & "$fixture\scripts\verify-package.ps1" -RequireSigned } 'cannot accept an unsigned build'
  '<Project><PropertyGroup><Version>0.3.0</Version></PropertyGroup></Project>' | Set-Content -LiteralPath "$fixture\Directory.Build.props" -Encoding utf8
  '{"Version":"0.3.0","Signed":false,"Artifacts":[{},{}]}' | Set-Content -LiteralPath "$fixture\artifacts\build-info.json" -Encoding utf8
  @((('0' * 64) + '  same.exe'), (('0' * 64) + '  same.exe')) | Set-Content -LiteralPath "$fixture\artifacts\SHA256SUMS.txt" -Encoding utf8
  Expect-Rejection { & "$fixture\scripts\verify-package.ps1" } 'Duplicate checksum path'
  Write-Output 'PASS: five signing/package fail-closed guards.'
} finally {
  Set-Location -LiteralPath $projectRoot
  $resolvedFixture = [IO.Path]::GetFullPath($fixture)
  if (!$resolvedFixture.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside project.' }
  Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
