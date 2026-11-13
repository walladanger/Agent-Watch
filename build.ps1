# Builds AgentWatch.exe (WPF, .NET Framework 4.x) with the csc.exe that ships with Windows. No installs, no admin.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$out = Join-Path $here 'AgentWatch.exe'
$src = Join-Path $here 'src'
$ico = Join-Path $here 'AgentWatch.ico'
$cargs = New-Object System.Collections.Generic.List[string]
foreach ($a in @('/nologo', '/target:winexe', '/optimize+', '/platform:x64', '/nowarn:0169,0414,0649', "/out:$out", "/win32icon:$ico")) { $cargs.Add($a) }
foreach ($r in @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll')) { $cargs.Add("/r:$fw\$r") }
foreach ($r in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) { $cargs.Add("/r:$wpf\$r") }
foreach ($x in @('Theme.xaml', 'Main.xaml')) { $cargs.Add("/resource:$src\$x,$x") }
$cargs.Add("/resource:$ico,AgentWatch.ico")
Get-ChildItem $src -Filter *.cs | ForEach-Object { $cargs.Add($_.FullName) }
& $csc $cargs.ToArray()
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Output "Built $out"
