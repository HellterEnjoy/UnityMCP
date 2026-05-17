# Installation

## Requirements

- Unity `2021.3+`
- Python `3.10+`
- An MCP-compatible client that can launch a local command-based server
- Windows PowerShell for the provided install script

## Unity Package Install From Git

1. Open your Unity project.
2. Open `Window > Package Manager`.
3. Click `+ > Add package from git URL...`.
4. Paste:

```text
https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main
```

5. Wait for Unity to compile the package.
6. Check `Tools > Unity MCP > Bridge > Status`.
7. If needed, run `Tools > Unity MCP > Bridge > Start`.

Equivalent `Packages/manifest.json` entry:

```json
{
  "dependencies": {
    "com.codex.unity-mcp": "https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main"
  }
}
```

The `?path=/unity-package/Packages/com.codex.unity-mcp` part is required because the Unity package
lives in a subfolder of this repository.

## Generic MCP Server Setup

The Unity bridge at `http://127.0.0.1:8765` is not an MCP server by itself. Your MCP client should
launch the Python MCP server from this repository, and that server talks to Unity.

From the repository root:

```powershell
.\scripts\setup-unity-mcp-server.ps1
```

That script:

- creates `server\.venv`
- installs the Python package
- leaves you with a reusable command for any MCP client

Typical launch command after setup:

```powershell
$python = (Resolve-Path .\server\.venv\Scripts\python.exe).Path
$env:UNITY_MCP_BRIDGE_URL = "http://127.0.0.1:8765"
& $python -m codex_unity_mcp.server
```

If your MCP client expects a registered server entry instead of a raw command, use:

- command: `.\server\.venv\Scripts\python.exe`
- args: `-m codex_unity_mcp.server`
- env: `UNITY_MCP_BRIDGE_URL=http://127.0.0.1:8765`

## Codex Install

Codex still has a one-command helper path:

```powershell
.\scripts\install-codex-mcp.ps1
```

That script:

- prepares the shared server environment
- registers `codex-unity` in Codex

Restart Codex after changing MCP configuration.

## Local Development Setup

Use this when you want Unity to consume the package directly from disk while you edit this repo.

1. Open your Unity project.
2. Open `Window > Package Manager`.
3. Click `+ > Add package from disk...`.
4. Select:

```text
<path-to-this-repo>\unity-package\Packages\com.codex.unity-mcp\package.json
```

If `Tools > Unity MCP` is missing, Unity did not compile the package successfully. Check the
Unity Console first.
