# Creates the autostart (Startup folder) and Desktop shortcuts for Agent Watch. No admin needed.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'AgentWatch.exe'
if (-not (Test-Path $exe)) { & (Join-Path $here 'build.ps1') }
$ws = New-Object -ComObject WScript.Shell
$targets = @(
  @{ Path = (Join-Path ([Environment]::GetFolderPath('Startup')) 'Agent Watch.lnk'); Args = '--hidden' },
  @{ Path = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Agent Watch.lnk'); Args = '' }
)
foreach ($t in $targets) {
  $s = $ws.CreateShortcut($t.Path)
  $s.TargetPath = $exe; $s.Arguments = $t.Args; $s.WorkingDirectory = $here
  $s.IconLocation = "$exe,0"; $s.Description = 'Agent Watch - live AI agents and screen-control helpers'
  $s.Save()
  Write-Output "Created $($t.Path)"
}
