$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
New-Item -ItemType Directory -Force -Path '.tools','.cache\temp' | Out-Null
$env:TEMP = Join-Path $projectRoot '.cache\temp'
$env:TMP = $env:TEMP
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools\dotnet-install.ps1'
$sdkVersion = (Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'global.json') | ConvertFrom-Json).sdk.version
& .\.tools\dotnet-install.ps1 -Version $sdkVersion -InstallDir (Join-Path $projectRoot '.tools\dotnet') -NoPath
if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
& "$PSScriptRoot\dev.ps1" build
