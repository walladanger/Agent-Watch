# Builds AgentWatch.exe with the built-in .NET Framework compiler (no installs, no admin).
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $here 'AgentWatch.exe'
& $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$out" "/win32icon:$(Join-Path $here 'AgentWatch.ico')" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll (Join-Path $here 'AgentWatch.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Output "Built $out"
