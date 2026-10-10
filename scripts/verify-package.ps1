param([switch]$RequireSigned)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $projectRoot
$info = Get-Content -Raw -LiteralPath 'artifacts\build-info.json' | ConvertFrom-Json
if ($RequireSigned -and !$info.Signed) { throw 'Public signed validation cannot accept an unsigned build.' }
$properties = [xml](Get-Content -Raw -LiteralPath 'Directory.Build.props')
if ($info.Version -ne $properties.Project.PropertyGroup.Version) { throw 'Package version does not match the source version.' }
$expected = @('artifacts/portable/win-x64/Clippboard.exe', "artifacts/installer/Clippboard-Setup-$($info.Version).exe")
if ($info.Artifacts.Count -ne 2) { throw 'Expected portable and setup artifacts.' }
$hashes = @{}
foreach ($line in Get-Content -LiteralPath 'artifacts\SHA256SUMS.txt') {
  if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw 'Invalid checksum manifest.' }
  if ($hashes.ContainsKey($Matches[2])) { throw 'Duplicate checksum path.' }
  $hashes[$Matches[2]] = $Matches[1]
}
if ($hashes.Count -ne 2) { throw 'Expected exactly two checksums.' }
foreach ($path in $expected) {
  $entry = @($info.Artifacts | Where-Object { $_.Path -eq $path })
  if ($entry.Count -ne 1 -or !$hashes.ContainsKey($path)) { throw "Missing artifact metadata: $path" }
  $actual = Get-FileHash -LiteralPath $path -Algorithm SHA256
  if ($actual.Hash -ne $hashes[$path]) { throw "Checksum mismatch: $path" }
  $signature = Get-AuthenticodeSignature -LiteralPath $path
  if ($signature.Status.ToString() -ne $entry[0].Status) { throw "Signature metadata mismatch: $path" }
  if ($info.Signed -or $RequireSigned) {
    if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -ne $entry[0].Publisher) { throw "Invalid or untimestamped signature: $path" }
  } elseif ($signature.Status -ne 'NotSigned' -or $entry[0].Publisher) { throw 'Unsigned evaluation metadata does not match the binary.' }
}
Write-Output "Verified $($info.Version): hashes and signature metadata match. Signed=$($info.Signed)"
