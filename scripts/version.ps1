function Get-ClippboardBuildVersion([string]$Root) {
  [xml]$properties = [IO.File]::ReadAllText((Join-Path $Root 'Directory.Build.props'))
  $source = [string]$properties.Project.PropertyGroup.Version
  $match = [regex]::Match($source, '^(\d+\.\d+\.\d+)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')
  if (!$match.Success) { throw 'Invalid source release version.' }
  $expectedWindows = $match.Groups[1].Value + '.0'
  foreach ($part in $expectedWindows.Split('.')) { if ([long]$part -gt 65535) { throw 'Windows version component exceeds its limit.' } }
  $windows = [string]$properties.Project.PropertyGroup.WindowsPackageVersion
  if (!$windows) { $windows = $expectedWindows }
  $file = [string]$properties.Project.PropertyGroup.FileVersion
  if (!$file) { $file = $windows }
  if ($windows -ne $expectedWindows -or $file -ne $windows) { throw 'Invalid or mismatched Windows package/file version.' }
  [pscustomobject]@{ Source = $source; Windows = $windows; File = $file }
}
