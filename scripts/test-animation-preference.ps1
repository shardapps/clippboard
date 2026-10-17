param([ValidateSet('Enable','Disable','Restore')][string]$Mode = 'Enable')
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
  throw 'Animation preference changes are restricted to disposable Windows GitHub Actions runners.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$snapshot = Join-Path $projectRoot '.cache/client-animation-original.json'
if (!('ClippboardCiAnimation' -as [type])) {
  Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class ClippboardCiAnimation {
  [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool Get(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
  [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool Set(uint action, uint parameter, nint value, uint flags);
}
'@
}
$previous = $false
if (![ClippboardCiAnimation]::Get(0x1042, 0, [ref]$previous, 0)) { throw 'Could not read client animation preference.' }
if ($Mode -eq 'Restore') {
  if (!(Test-Path -LiteralPath $snapshot)) { Write-Output 'No animation preference snapshot to restore.'; return }
  $savedPreference = [IO.File]::ReadAllText($snapshot) | ConvertFrom-Json
  $enabled = [bool]$savedPreference.Enabled
} else {
  if (!(Test-Path -LiteralPath $snapshot)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $snapshot) | Out-Null
    [IO.File]::WriteAllText($snapshot, (@{ Enabled = $previous } | ConvertTo-Json -Compress))
  }
  $enabled = $Mode -eq 'Enable'
}
# SPI_SETCLIENTAREAANIMATION takes the BOOL value, not a pointer. Broadcast
# the session change without writing a permanent user-profile preference.
if (![ClippboardCiAnimation]::Set(0x1043, 0, [IntPtr]([int]$enabled), 2)) { throw 'Could not set client animation preference.' }
$actual = $false
if (![ClippboardCiAnimation]::Get(0x1042, 0, [ref]$actual, 0) -or $actual -ne $enabled) { throw 'Animation preference did not apply.' }
Write-Output "Client area animations: $actual ($Mode; isolated CI desktop only)."
