[CmdletBinding()]
param([string]$Distribution = 'Ubuntu')
$ErrorActionPreference = 'Continue'
# Read-only probe. wsl.exe receives discrete arguments; no shell string is passed to Ubuntu.
$commands = @(
  @('claude','--version'), @('claude','agents','--json','--all'),
  @('claude','daemon','status'), @('claude','auth','status'), @('claude','--bg','--help')
)
foreach ($argv in $commands) {
  $full = @('-d',$Distribution,'--') + $argv
  Write-Host (">>> wsl.exe " + ($full -join ' '))
  & wsl.exe $full 2>&1
  Write-Host ("exit=" + $LASTEXITCODE)
}
Write-Host 'Review output for secrets before saving raw JSON.'
