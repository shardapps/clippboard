$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.cache/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$dotnetPath = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$json = & $dotnetPath package list --project Clippboard.slnx --include-transitive --vulnerable --format json --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Dependency vulnerability query failed.' }
$report = ($json -join [Environment]::NewLine) | ConvertFrom-Json
[xml]$solution = [IO.File]::ReadAllText((Join-Path $projectRoot 'Clippboard.slnx'))
$expectedProjects = @($solution.SelectNodes('//Project') | ForEach-Object { [IO.Path]::GetFullPath($_.GetAttribute('Path'), $projectRoot) })
$reportedProjects = @($report.projects | ForEach-Object { [IO.Path]::GetFullPath($_.path, $projectRoot) })
if ($expectedProjects.Count -eq 0 -or $reportedProjects.Count -ne $expectedProjects.Count -or
    @($reportedProjects | Select-Object -Unique).Count -ne $expectedProjects.Count -or
    @(Compare-Object $expectedProjects $reportedProjects).Count -ne 0 -or
    @($report.sources).Count -eq 0 -or $report.problems) { throw 'Incomplete dependency audit report.' }
$findings = @($report.projects | ForEach-Object { $_.frameworks } | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } | Where-Object { $_.vulnerabilities })
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'artifacts') | Out-Null
# Store only repository-relative project names in the optional CI artifact.
foreach ($project in $report.projects) { $project.path = [IO.Path]::GetRelativePath($projectRoot, $project.path).Replace('\', '/') }
[IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts/dependency-audit.json'), ($report | ConvertTo-Json -Depth 20))
if ($findings.Count -gt 0) { throw "Known vulnerable NuGet graph entries: $($findings.Count). Review artifacts/dependency-audit.json." }
Write-Output 'PASS: no vulnerable direct/transitive entries reported by the configured NuGet audit source. Framework/download packs need separate servicing review.'
