$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$ServerDir = Join-Path $RepoRoot "server"
$VenvPython = Join-Path $ServerDir ".venv\Scripts\python.exe"

if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
    throw "Codex CLI was not found in PATH. Install or launch Codex first, then rerun this script."
}

. (Join-Path $ScriptDir "setup-unity-mcp-server.ps1")

codex mcp remove codex-unity 2>$null | Out-Null

& "codex" @(
    "mcp",
    "add",
    "codex-unity",
    "--env",
    "UNITY_MCP_BRIDGE_URL=http://127.0.0.1:8765",
    "--",
    $VenvPython,
    "-m",
    "codex_unity_mcp.server"
)
if ($LASTEXITCODE -ne 0) {
    throw "codex mcp add failed with exit code $LASTEXITCODE"
}

& "codex" @("mcp", "get", "codex-unity")
if ($LASTEXITCODE -ne 0) {
    throw "codex mcp get failed with exit code $LASTEXITCODE"
}
