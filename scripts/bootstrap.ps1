$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
New-Item -ItemType Directory -Force -Path '.tools','.cache\temp' | Out-Null
$env:TEMP = Join-Path $projectRoot '.cache\temp'
$env:TMP = $env:TEMP
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools\dotnet-install.ps1'
& .\.tools\dotnet-install.ps1 -Channel 10.0 -InstallDir (Join-Path $projectRoot '.tools\dotnet') -NoPath
if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
& "$PSScriptRoot\dev.ps1" build
