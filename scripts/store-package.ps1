param([string]$SdkDirectory, [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.cache\nuget'
$env:TEMP = Join-Path $projectRoot '.cache\temp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
if (!$SdkDirectory) {
  $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  $SdkDirectory = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object { $_.Name -match '^10\.0\.' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')) } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
  $SdkDirectory = Join-Path $SdkDirectory 'x64'
}
$makeappx = Join-Path $SdkDirectory 'makeappx.exe'
if (!(Test-Path -LiteralPath $makeappx)) { throw 'Windows SDK MakeAppx is required.' }
. "$PSScriptRoot/version.ps1"
$versions = Get-ClippboardBuildVersion $projectRoot
$version = $versions.Source
$windowsVersion = $versions.Windows
$stage = Join-Path $projectRoot "artifacts\store\$version\stage"
$output = Join-Path $projectRoot "artifacts\store\$version\Clippboard-$version-x64.msix"
# A fresh layout prevents stale runtime files from entering a subsequent package.
if (Test-Path -LiteralPath $stage) { throw 'Store staging directory exists. Use a fresh version or remove only that reviewed build output before rebuilding.' }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'app') | Out-Null
if (!$SkipPublish) {
  $dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
  if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
  & $dotnetPath publish src/Clippboard.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:ClippboardPublishProfile=store -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $stage 'app')
  if ($LASTEXITCODE -ne 0) { throw 'Store publish failed.' }
} else {
  Copy-Item -LiteralPath 'artifacts\portable\win-x64\Clippboard.exe' -Destination (Join-Path $stage 'app\Clippboard.exe')
  Copy-Item -LiteralPath 'artifacts\portable\win-x64\licenses' -Destination (Join-Path $stage 'app\licenses') -Recurse
}
& "$PSScriptRoot/verify-notices.ps1" -Root (Join-Path $stage 'app')
& "$PSScriptRoot\brand.ps1" -StoreAssetDirectory (Join-Path $stage 'Assets')
[xml]$manifest = [IO.File]::ReadAllText((Join-Path $projectRoot 'installer/store/AppxManifest.xml'))
$manifest.Package.Identity.Version = $windowsVersion
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))
& $makeappx pack /d $stage /p $output /o
if ($LASTEXITCODE -ne 0) { throw 'MSIX schema/content validation failed.' }
$hash = Get-FileHash -LiteralPath $output -Algorithm SHA256
[ordered]@{ Version = $windowsVersion; PackageName = $manifest.Package.Identity.Name; Publisher = $manifest.Package.Identity.Publisher; PublisherDisplayName = 'ShardCoders'; Architecture = 'x64'; SHA256 = $hash.Hash.ToLowerInvariant(); Signed = $false; Distribution = 'Microsoft Store submission only; Store signing requires certification' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $output) 'package-info.json') -Encoding utf8
Write-Output "Validated unsigned Store submission package: $output"
