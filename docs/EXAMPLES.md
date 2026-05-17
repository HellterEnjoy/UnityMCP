# Examples

The repository already includes example config files here:

- `examples\codex-mcp.example.json`
- `examples\codex-config.example.toml`
- `examples\generic-mcp-command.example.txt`
- `examples\unity-manifest-git.example.json`

## Typical Setup Targets

### Unity Package From Git

Use:

```text
https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main
```

### Generic MCP Command

After running:

```powershell
.\scripts\setup-unity-mcp-server.ps1
```

use this command in any MCP client that accepts a local command + env configuration:

```text
command: <repo>\server\.venv\Scripts\python.exe
args: -m codex_unity_mcp.server
env: UNITY_MCP_BRIDGE_URL=http://127.0.0.1:8765
```

There is also a ready text template in:

- `examples\generic-mcp-command.example.txt`

### Codex MCP Registration

From the repository root:

```powershell
.\scripts\install-codex-mcp.ps1
```

### Manual MCP Configuration

See:

- `examples\codex-mcp.example.json`
- `examples\codex-config.example.toml`

### Agent Bootstrap

If a model is weak at discovering the available Unity tools, point it at:

- `docs\AGENT_INSTRUCTIONS.md`
- `get_unity_capabilities()`
- `unity://capabilities`
