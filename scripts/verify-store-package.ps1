param([string]$Path)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$properties = [xml][IO.File]::ReadAllText((Join-Path $projectRoot 'Directory.Build.props'))
$version = $properties.Project.PropertyGroup.Version
if (!$Path) { $Path = Join-Path $projectRoot "artifacts\store\$version\Clippboard-$version-x64.msix" }
$package = [IO.Compression.ZipFile]::OpenRead($Path)
try {
  $entry = $package.GetEntry('AppxManifest.xml')
  if (!$entry -or $entry.Length -gt 65536) { throw 'Missing or oversized package manifest.' }
  $reader = [IO.StreamReader]::new($entry.Open())
  try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
  if ($manifest.Package.Identity.Name -ne 'ShardCoders.Clippboard' -or $manifest.Package.Identity.Publisher -ne 'CN=D11F3529-FC83-43F5-B8D5-F94F40D30E7D' -or $manifest.Package.Identity.Version -ne "$version.0" -or $manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Package does not match the reserved Store identity/version.' }
  foreach ($name in @('Assets/Logo44.png','Assets/Logo50.png','Assets/Logo150.png','app/Clippboard.exe','AppxBlockMap.xml','[Content_Types].xml')) { if (!$package.GetEntry($name)) { throw "Missing package component: $name" } }
  $noticeSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets/licenses') -File | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Source = $_.FullName } })
  $noticeSources += [pscustomobject]@{ Name = 'LICENSE-CLIPPBOARD.txt'; Source = Join-Path $projectRoot 'LICENSE' }
  foreach ($item in $noticeSources) {
    $notice = $package.GetEntry('app/licenses/' + $item.Name)
    if (!$notice -or $notice.Length -le 0 -or $notice.Length -gt 1048576) { throw "Missing or oversized Store notice: $($item.Name)" }
    $noticeStream = $notice.Open()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $noticeHash = [BitConverter]::ToString($hasher.ComputeHash($noticeStream)).Replace('-', '') } finally { $hasher.Dispose(); $noticeStream.Dispose() }
    if ($noticeHash -ne (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash) { throw "Store notice differs from source: $($item.Name)" }
  }
  if ($manifest.Package.Applications.Application.Extensions.Extension.StartupTask.Enabled -ne 'false') { throw 'Store startup must be opt-in.' }
  if ($package.GetEntry('AppxSignature.p7x')) { throw 'Store submission should be unsigned; do not distribute this build as a trusted sideload.' }
  $info = Get-Content -Raw -LiteralPath (Join-Path (Split-Path -Parent $Path) 'package-info.json') | ConvertFrom-Json
  if ($info.Signed -or $info.SHA256 -ne (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Store checksum/status metadata does not match.' }
} finally { $package.Dispose() }
Write-Output 'PASS: Store identity, version, required assets/runtime, opt-in startup, unsigned status and SHA-256.'
