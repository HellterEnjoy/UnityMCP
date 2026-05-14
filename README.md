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
- Create, delete, and duplicate GameObjects with Undo support.
- Add and remove GameObject components with Undo support.
- Capture scene snapshots, diff scene changes, and run safe rollback-capable edit batches.
- Enter and exit Play Mode, plus poll Play Mode state across bridge restarts.
- Invoke Unity editor menu items and trigger Unity Test Runner runs.
- Read and write serialized component fields, including during Play Mode.
- Send keyboard and mouse input to the Unity Game view.
- Wait for objects, logs, scene changes, and component field values.
- Set a GameObject transform with Undo support.

This is intentionally small. It is a stable base for expanding toward a Cursor-like Unity workflow instead of a large collection of brittle commands.

## Unity Setup From Git

1. Open your Unity project.
2. Open `Window > Package Manager`.
3. Click `+ > Add package from git URL...`.
4. Enter:

   ```text
   https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main
   ```

5. Unity should download and compile the package.
6. Use `Window > Codex MCP Bridge > Status` to confirm it is running.
7. If needed, use `Window > Codex MCP Bridge > Start`.

Equivalent `Packages/manifest.json` entry:

```json
{
  "dependencies": {
    "com.codex.unity-mcp": "https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main"
  }
}
```

The package lives in a repository subfolder, so the `?path=/unity-package/Packages/com.codex.unity-mcp`
part is required. The `#main` revision keeps the dependency pointed at the main branch.

## Updating The Unity Package

Unity locks Git dependencies to a specific commit in `Packages/packages-lock.json`.
To update to the latest `main` branch commit, use one of these options:

- In Unity, run `Window > Codex MCP Bridge > Update Package From Git`.
- In `Window > Package Manager`, select `Codex Unity MCP Bridge` and click `Update` if Unity shows one.
- Use `+ > Add package from git URL...` again with the same URL.

You do not need to remove and re-add the package.

## Local Unity Setup For Development

Use this only when you are editing this repository locally and want Unity to read the package directly from disk.

1. Open your Unity project.
2. Open `Window > Package Manager`.
3. Click `+ > Add package from disk...`.
4. Select:

   ```text
   <path-to-this-repo>\unity-package\Packages\com.codex.unity-mcp\package.json
   ```

5. Unity should compile the package.

If `Window > Codex MCP Bridge` is missing, check that the package is listed in
`Window > Package Manager` and that the Unity Console has no compile errors.
Unity only creates the menu after the Editor script compiles successfully.

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
cd <path-to-this-repo>\server
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -e .
python -m codex_unity_mcp.server
```

The MCP server communicates over stdio, so it is normally launched by Codex or another MCP client, not manually.

## Safe Edit Mode

Safe Edit Mode is the project-specific workflow for agentic Unity edits. Instead of firing unrelated
commands blindly, Codex can:

1. Capture a scene snapshot.
2. Run a batch of allowed scene-edit commands in one Unity Undo group.
3. Roll the whole batch back automatically if a command fails.
4. Return a structural diff showing added, removed, and changed GameObjects.

Example batch command payload:

```json
[
  {
    "tool": "create_gameobject",
    "params": {
      "name": "SafeEditCube",
      "primitive_type": "cube",
      "position": [0, 1, 0]
    }
  },
  {
    "tool": "add_component",
    "params": {
      "path": "SafeEditCube",
      "component_type": "Rigidbody"
    }
  }
]
```

## Semi-Realtime Game Testing

The bridge now supports a lightweight gameplay loop for agentic testing:

1. Enter Play Mode.
2. Poll Play Mode state until Unity has finished reloading.
3. Send keyboard or mouse input to the Game view.
4. Read or write runtime component fields.
5. Wait for logs, scene changes, object existence, or field values.
6. Optionally run Unity Test Runner and poll the run status.

This is not a video stream or a direct in-process scripting shell. The MCP server works through
fast repeated bridge requests and reconnect-tolerant polling, which is enough for semi-realtime
test orchestration without embedding Codex inside the Unity process.

## Codex App Setup

The Unity bridge at `http://127.0.0.1:8765` is not an MCP server. Do not add that URL directly to
Codex. Codex should launch the Python MCP server from this repository, and that server talks to the
Unity bridge internally.

From the repository root:

```powershell
.\scripts\install-codex-mcp.ps1
```

This creates `server\.venv`, installs the Python MCP server, and registers `codex-unity` with the
Codex CLI/app.

Equivalent manual setup:

```powershell
cd <path-to-this-repo>\server
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e .
$python = (Resolve-Path .\.venv\Scripts\python.exe).Path
codex mcp add codex-unity --env UNITY_MCP_BRIDGE_URL=http://127.0.0.1:8765 -- $python -m codex_unity_mcp.server
```

If you edit `~\.codex\config.toml` by hand, use the TOML form in:

```text
examples\codex-config.example.toml
```

Restart Codex after changing MCP configuration so the app reloads the server list.

## Generic MCP Config Example

See:

```text
examples\codex-mcp.example.json
```

Set `cwd` to the absolute path of the `server` directory on your machine:

```json
{
  "mcpServers": {
    "codex-unity": {
      "command": "python",
      "args": ["-m", "codex_unity_mcp.server"],
      "cwd": "<path-to-this-repo>\\server",
      "env": {
        "UNITY_MCP_BRIDGE_URL": "http://127.0.0.1:8765"
      }
    }
  }
}
```

If your MCP client does not support `cwd`, use the venv Python executable and module form:

```json
{
  "mcpServers": {
    "codex-unity": {
      "command": "<path-to-this-repo>\\server\\.venv\\Scripts\\python.exe",
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
- `create_gameobject(name="GameObject", primitive_type="empty", parent_instance_id=None, parent_name=None, parent_path=None, position=None, rotation=None, scale=None)`
- `delete_gameobject(instance_id=None, name=None, path=None)`
- `duplicate_gameobject(instance_id=None, name=None, path=None, new_name=None, parent_instance_id=None, parent_name=None, parent_path=None, position=None, rotation=None, scale=None)`
- `add_component(component_type, instance_id=None, name=None, path=None, allow_multiple=False)`
- `remove_component(component_type, instance_id=None, name=None, path=None, remove_all=False)`
- `snapshot_scene(snapshot_id=None)`
- `diff_scene(before_snapshot_id, after_snapshot_id=None)`
- `safe_batch(commands, rollback_on_error=True, label="Codex MCP Safe Batch")`
- `enter_play_mode()`
- `exit_play_mode()`
- `get_play_state()`
- `invoke_menu_item(menu_path)`
- `run_unity_tests(mode="editmode", assembly_names=None, test_names=None, group_names=None)`
- `get_unity_test_status(run_id=None)`
- `get_component_field(component_type, property_path, instance_id=None, name=None, path=None, component_index=0)`
- `set_component_field(component_type, property_path, value, instance_id=None, name=None, path=None, component_index=0)`
- `send_keyboard_input(key, event_type="press", character=None)`
- `send_mouse_input(x, y, event_type="click", button=0)`
- `click_ui_element(instance_id=None, name=None, path=None, button=0)`
- `wait_for_object(instance_id=None, name=None, path=None, exists=True, timeout_ms=5000, poll_ms=100)`
- `wait_for_log(text, log_type=None, since_seconds=60.0, timeout_ms=5000, poll_ms=100)`
- `wait_for_scene(scene_name=None, scene_path=None, timeout_ms=5000, poll_ms=100)`
- `wait_for_component_field(component_type, property_path, expected, instance_id=None, name=None, path=None, component_index=0, comparison="equals", timeout_ms=5000, poll_ms=100)`
- `wait_for_play_mode(is_playing=True, is_paused=None, timeout_ms=10000, poll_ms=100)`
- `wait_for_unity_tests(require_success=True, timeout_ms=60000, poll_ms=250)`
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

1. Add richer UI-object targeting beyond center-point clicks.
2. Add runtime assertions for profiler counters, animation state, and physics contacts.
3. Add prefab-aware inspection and overrides.
4. Add AssetDatabase search.
5. Add screenshot diffing and visual regression baselines.
