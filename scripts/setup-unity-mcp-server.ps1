$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$ServerDir = Join-Path $RepoRoot "server"
$VenvDir = Join-Path $ServerDir ".venv"
$VenvPython = Join-Path $ServerDir ".venv\Scripts\python.exe"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $VenvPython)) {
    Invoke-Checked -FilePath "python" -Arguments @("-m", "venv", $VenvDir)
}

Invoke-Checked -FilePath $VenvPython -Arguments @("-m", "pip", "install", "-e", $ServerDir)

Write-Host "Unity MCP server environment is ready."
Write-Host "Python: $VenvPython"
Write-Host "Run your MCP client with UNITY_MCP_BRIDGE_URL=http://127.0.0.1:8765 and:"
Write-Host "  $VenvPython -m codex_unity_mcp.server"
