$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\src\AgentSupervisor.App\bin\Release\net10.0-windows\win-x64\publish\AgentSupervisor.App.exe'
$p = Start-Process $exe -PassThru
try { Start-Sleep -Seconds 2; $r = Invoke-WebRequest 'http://127.0.0.1:8700/health'; if ($r.StatusCode -ne 200) { throw 'health failed' } } finally { if (!$p.HasExited) { Stop-Process $p.Id } }
