param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $projectRoot
# Assembles evaluation assets only. No signing, tag, release or Store publication.
& "$PSScriptRoot/verify-package.ps1"
$info = [IO.File]::ReadAllText((Join-Path $projectRoot 'artifacts/build-info.json')) | ConvertFrom-Json
$version = $info.Version
$output = Join-Path $projectRoot "artifacts/candidate/$version"
if (Test-Path -LiteralPath $output) { throw 'Candidate output must be fresh; refusing to replace existing assets.' }
New-Item -ItemType Directory -Path $output | Out-Null
$portable = Join-Path $projectRoot 'artifacts/portable/win-x64'
$zipPath = Join-Path $output "Clippboard-Portable-$version-win-x64.zip"
# Include the tested executable and notices, without intermediate build/PDB files.
Compress-Archive -LiteralPath (Join-Path $portable 'Clippboard.exe'), (Join-Path $portable 'licenses') -DestinationPath $zipPath
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
  $expected = @([pscustomobject]@{ Name = 'Clippboard.exe'; Path = Join-Path $portable 'Clippboard.exe' })
  $expected += Get-ChildItem -LiteralPath (Join-Path $portable 'licenses') -File | ForEach-Object { [pscustomobject]@{ Name = 'licenses/' + $_.Name; Path = $_.FullName } }
  $files = @($zip.Entries | Where-Object { $_.Name })
  if ($files.Count -ne $expected.Count) { throw 'Unexpected portable ZIP contents.' }
  foreach ($item in $expected) {
    $entry = @($files | Where-Object { $_.FullName.Replace('\', '/') -ceq $item.Name })
    if ($entry.Count -ne 1 -or $entry[0].Length -ne (Get-Item -LiteralPath $item.Path).Length) { throw "Missing or unexpected ZIP entry: $($item.Name)" }
    $stream = $entry[0].Open(); $hasher = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose(); $hasher.Dispose() }
    if ($hash -ne (Get-FileHash -LiteralPath $item.Path).Hash) { throw "Portable ZIP byte mismatch: $($item.Name)" }
  }
} finally { $zip.Dispose() }
$setupName = "Clippboard-Setup-$version.exe"
Copy-Item -LiteralPath (Join-Path $projectRoot "artifacts/installer/$setupName") -Destination (Join-Path $output $setupName)
if ((Get-FileHash -LiteralPath (Join-Path $output $setupName)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $projectRoot "artifacts/installer/$setupName")).Hash) { throw 'Candidate setup copy differs from the tested installer.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts/build-info.json') -Destination $output
$commit = & git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record candidate source commit.' }
$tree = & git rev-parse 'HEAD^{tree}'
if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record candidate source tree.' }
$head = $commit
if ($env:GITHUB_EVENT_PATH -and (Test-Path -LiteralPath $env:GITHUB_EVENT_PATH)) {
  $eventData = [IO.File]::ReadAllText($env:GITHUB_EVENT_PATH) | ConvertFrom-Json
  if ($eventData.pull_request.head.sha) { $head = $eventData.pull_request.head.sha }
}
$run = if ($env:GITHUB_RUN_ID) { "https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
[ordered]@{
  Version = $version; SourceHead = $head; BuildCommit = $commit; BuildTree = $tree; WorkflowRun = $run
  ExecutableProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $portable 'Clippboard.exe')).ProductVersion
  Signed = [bool]$info.Signed
  Status = 'Validation candidate; publication held for device and Store acceptance. This JSON is a build record, not a signed attestation.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8
Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
  "$((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Output "PASS: candidate portable ZIP bytes, installer, notices, source provenance and SHA-256 manifest: $output"
