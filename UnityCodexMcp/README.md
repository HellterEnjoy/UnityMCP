# Codex Unity MCP

Minimal Unity MCP prototype for giving Codex live access to the current Unity Editor scene.

The project is split into two parts:

- `unity-package/Packages/com.codex.unity-mcp`: Unity Editor package with a localhost HTTP bridge.
- `server`: Python MCP server that exposes bridge endpoints as MCP tools/resources.

## Current Capabilities

- Check Unity bridge health.
- Read Unity Editor state.
- Read active scene hierarchy.
- Find GameObjects by name, path, tag, or component.
- Inspect a GameObject, including transform, components, and serialized primitive properties.
- Read recent Unity Console entries through Unity's internal reflection API.
- Capture a screenshot from the Scene View or Main Camera.
- Set a GameObject transform with Undo support.

This is intentionally small. It is a stable base for expanding toward a Cursor-like Unity workflow instead of a large collection of brittle commands.

## Unity Setup

1. Open your Unity project.
2. Open `Window > Package Manager`.
3. Click `+ > Add package from disk...`.
4. Select:

   ```text
   A:\Вопросы\UnityCodexMcp\unity-package\Packages\com.codex.unity-mcp\package.json
   ```

5. Unity should compile the package.
6. Use `Tools > Codex MCP Bridge > Status` to confirm it is running.
7. If needed, use `Tools > Codex MCP Bridge > Start`.

The bridge listens only on:

```text
http://127.0.0.1:8765
```

Quick browser/manual checks:

```text
http://127.0.0.1:8765/health
http://127.0.0.1:8765/editor/state
http://127.0.0.1:8765/scene/hierarchy?includeInactive=true&maxNodes=100
```

## MCP Server Setup

From PowerShell:

```powershell
cd A:\Вопросы\UnityCodexMcp\server
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -e .
python -m codex_unity_mcp.server
```

The MCP server communicates over stdio, so it is normally launched by Codex or another MCP client, not manually.

## Codex MCP Config Example

See:

```text
A:\Вопросы\UnityCodexMcp\examples\codex-mcp.example.json
```

If your MCP client does not support `cwd`, use the venv Python executable and module form:

```json
{
  "mcpServers": {
    "codex-unity": {
      "command": "A:\\Вопросы\\UnityCodexMcp\\server\\.venv\\Scripts\\python.exe",
      "args": ["-m", "codex_unity_mcp.server"],
      "env": {
        "UNITY_MCP_BRIDGE_URL": "http://127.0.0.1:8765"
      }
    }
  }
}
```

## MCP Tools

- `unity_health()`
- `get_editor_state()`
- `get_scene_hierarchy(include_inactive=True, max_nodes=500)`
- `find_gameobjects(query="", mode="name", include_inactive=True, limit=50)`
- `inspect_gameobject(instance_id=None, name=None, path=None, include_properties=True)`
- `set_transform(instance_id=None, name=None, path=None, position=None, rotation=None, scale=None)`
- `read_console(count=50)`
- `take_scene_screenshot(source="scene_view", include_image=False, max_resolution=512)`

## MCP Resources

- `unity://editor/state`
- `unity://scene/hierarchy`

## Design Notes

- Unity APIs must run on Unity's main thread. The bridge queues HTTP requests and processes them during `EditorApplication.update`.
- Responses are JSON and intentionally capped where needed to avoid flooding the model context.
- `set_transform` uses `Undo.RecordObject` and marks the scene dirty.
- The Console reader uses Unity internal reflection. If Unity changes those internals, the endpoint returns a clear error instead of crashing the bridge.
- The HTTP bridge is local-only. Do not expose it on a public interface without authentication and command restrictions.

## Useful Next Extensions

1. Add `create_gameobject` and `delete_gameobject` with Undo.
2. Add `add_component` and `remove_component`.
3. Add safe serialized property writes.
4. Add prefab-aware inspection and overrides.
5. Add AssetDatabase search.
6. Add play mode controls.
7. Add test runner endpoints.
