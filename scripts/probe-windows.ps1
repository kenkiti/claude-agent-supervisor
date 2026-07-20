$ErrorActionPreference = 'Continue'
$captureTimestamp = Get-Date -Format 'yyyy-MM-dd'
$fixtureDir = Join-Path $PSScriptRoot '..\fixtures\claude-agent-view\windows'
New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null

$version = (& claude --version 2>&1 | Out-String).Trim()
$cliVersion = $version

function Save-ProbeResult([string]$name, [string]$command, $result, [int]$exitCode = 0) {
    $record = [ordered]@{
        'capture-timestamp' = $captureTimestamp
        'claude-cli-version' = $cliVersion
        command = $command
        exitCode = $exitCode
        result = $result
    }
    $record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixtureDir $name) -Encoding utf8
}

Save-ProbeResult 'version.json' 'claude --version' $version $LASTEXITCODE

$agentsText = (& claude agents --json --all 2>&1 | Out-String).Trim()
try { $agents = $agentsText | ConvertFrom-Json } catch { $agents = $agentsText }
Save-ProbeResult 'agents.json' 'claude agents --json --all' $agents $LASTEXITCODE

$daemonText = (& claude daemon status 2>&1 | Out-String).Trim()
Save-ProbeResult 'daemon-status.json' 'claude daemon status' $daemonText $LASTEXITCODE

$authText = (& claude auth status 2>&1 | Out-String).Trim()
try {
    $auth = $authText | ConvertFrom-Json
    foreach ($field in @('email', 'orgId', 'orgName')) {
        if ($null -ne $auth.PSObject.Properties[$field]) { $auth.$field = '<REDACTED>' }
    }
} catch { $auth = $authText }
Save-ProbeResult 'auth-status.json' 'claude auth status' $auth $LASTEXITCODE
