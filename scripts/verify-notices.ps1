param([string]$Root)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (!$Root) { $Root = Join-Path $projectRoot 'artifacts/portable/win-x64' }
$expected = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets/licenses') -File | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Source = $_.FullName } })
$expected += [pscustomobject]@{ Name = 'LICENSE-CLIPPBOARD.txt'; Source = Join-Path $projectRoot 'LICENSE' }
foreach ($item in $expected) {
  $target = Join-Path $Root ('licenses/' + $item.Name)
  if (!(Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash) { throw "Missing or altered bundled notice: $($item.Name)" }
}
Write-Output "PASS: $($expected.Count) bundled licenses/notices match their source copies."
