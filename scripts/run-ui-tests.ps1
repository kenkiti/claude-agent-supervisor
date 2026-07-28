$ErrorActionPreference = 'Stop'
$app = Start-Process dotnet -ArgumentList 'run','--project','src/AgentSupervisor.App/AgentSupervisor.App.csproj','--no-launch-profile' -PassThru
try {
  $ready = $false
  for ($i = 0; $i -lt 30; $i++) { try { Invoke-WebRequest http://127.0.0.1:8700/health -UseBasicParsing | Out-Null; $ready = $true; break } catch { Start-Sleep -Seconds 1 } }
  if (-not $ready) { throw 'AgentSupervisor did not become healthy' }
  $env:AGENTSUPERVISOR_UI_BASE_URL = 'http://127.0.0.1:8700'
  dotnet test tests/AgentSupervisor.UiTests/AgentSupervisor.UiTests.csproj --configuration Release
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
