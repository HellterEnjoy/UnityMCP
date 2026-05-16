# Codex Unity MCP

Unity automation bridge for Codex.

This repository gives Codex a practical way to work with a live Unity project:

- inspect scenes and objects
- edit the scene safely with rollback
- enter Play Mode and drive lightweight gameplay checks
- read and write runtime component fields
- take screenshots and verify results step by step

It is not a generic remote-control toy. The goal is a usable agent workflow for real Unity work.

## Why Install This

If you want Codex to do more than generate scripts blindly, this repository gives it actual editor
and runtime feedback:

- scene and hierarchy visibility
- structured GameObject and component inspection
- safe multi-step scene edits
- semi-realtime gameplay testing
- Unity Test Runner integration

That makes the loop closer to:

`inspect -> change -> verify -> recover`

instead of:

`guess -> write code -> hope`

## Project Layout

- `unity-package/Packages/com.codex.unity-mcp`
  Unity Editor package with the localhost bridge.
- `server`
  Python MCP server that exposes the bridge as Codex tools and resources.
- [ROADMAP.md](ROADMAP.md)
  Development roadmap and planned phases.

## Project Status

`Experimental / early alpha`

The repository is already useful and installable, but the API and editor workflows are still
moving. Expect new capabilities and some interface changes before `1.0`.

## License

This repository is not released under MIT or another permissive open-source license.

Current license model:

- personal use is allowed
- personal modifications are allowed
- redistribution, public forks, republishing, and commercial use require prior agreement with the author

See [LICENSE](LICENSE).

## Requirements

- Unity `2021.3+`
- Python `3.10+`
- Codex CLI / Codex app with MCP support
- Windows PowerShell for the provided install script

## Current Pillars

### 1. Safe Scene Editing

- inspect scene hierarchy and GameObjects
- create, delete, duplicate, and transform objects
- add and remove components
- use `snapshot -> safe_batch -> diff` for rollback-capable edits

### 2. Semi-Realtime Game Testing

- enter and exit Play Mode
- poll Play Mode state across Unity bridge restarts
- send keyboard and mouse input to the Game view
- read and write serialized component fields during runtime
- wait for logs, objects, scenes, fields, and Play Mode state

### 3. Visual Verification

- capture Scene View screenshots
- capture Main Camera screenshots
- capture Unity editor pane screenshots
- read Unity Console output
- combine screenshots with structured state checks

### 4. Editor Ergonomics

- focus and open common Unity windows
- select and ping scene objects
- select and open assets from the Project view
- use editor-pane screenshots as part of the agent loop

## Quick Install In Unity

Security warning:
The bridge is local-only by design and listens on `127.0.0.1:8765`. Do not expose it on a public
network interface without authentication and tighter command restrictions.

1. Open your Unity project.
2. Open `Window > Package Manager`.
3. Click `+ > Add package from git URL...`.
4. Paste:

```text
https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main
```

5. Wait for Unity to compile the package.
6. Check `Window > Codex MCP Bridge > Status`.
7. If needed, run `Window > Codex MCP Bridge > Start`.

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

## Quick Install In Codex

The Unity bridge at `http://127.0.0.1:8765` is not an MCP server by itself. Codex should launch the
Python MCP server from this repository, and that server talks to Unity.

From the repository root:

```powershell
.\scripts\install-codex-mcp.ps1
```

That script:

- creates `server\.venv`
- installs the Python package
- registers `codex-unity` in Codex

Manual setup:

```powershell
cd <path-to-this-repo>\server
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e .
$python = (Resolve-Path .\.venv\Scripts\python.exe).Path
codex mcp add codex-unity --env UNITY_MCP_BRIDGE_URL=http://127.0.0.1:8765 -- $python -m codex_unity_mcp.server
```

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

If `Window > Codex MCP Bridge` is missing, Unity did not compile the package successfully. Check the
Unity Console first.

The bridge listens only on:

```text
http://127.0.0.1:8765
```

Quick manual checks:

```text
http://127.0.0.1:8765/health
http://127.0.0.1:8765/editor/state
http://127.0.0.1:8765/scene/hierarchy?includeInactive=true&maxNodes=100
```

## Core Workflows

### Safe Edit Mode

Safe Edit Mode is the current project-specific differentiator.

Instead of firing unrelated scene-edit commands one by one, Codex can:

1. capture a snapshot
2. apply a batch of changes
3. roll the whole batch back if one command fails
4. inspect the resulting structural diff

Example batch payload:

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

### Semi-Realtime Testing

The runtime testing loop is lightweight but already practical:

1. enter Play Mode
2. wait until Unity finishes reloading
3. send input to the Game view
4. inspect or change runtime values
5. wait for expected logs or state changes
6. exit Play Mode or run Unity tests

This is not a video stream. It is a reconnect-tolerant request loop that is fast enough for
step-by-step agent testing.

## MCP Tools

### Scene And Editor

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

### Safe Edit

- `snapshot_scene(snapshot_id=None)`
- `diff_scene(before_snapshot_id, after_snapshot_id=None)`
- `safe_batch(commands, rollback_on_error=True, label="Codex MCP Safe Batch")`

### Gameplay And Runtime

- `enter_play_mode()`
- `exit_play_mode()`
- `get_play_state()`
- `get_component_field(component_type, property_path, instance_id=None, name=None, path=None, component_index=0)`
- `set_component_field(component_type, property_path, value, instance_id=None, name=None, path=None, component_index=0)`
- `send_keyboard_input(key, event_type="press", character=None)`
- `send_mouse_input(x, y, event_type="click", button=0)`
- `click_ui_element(instance_id=None, name=None, path=None, button=0)`

### Editor Ergonomics

- `take_editor_screenshot(target="active_window", include_image=False, max_resolution=1400)`
- `focus_editor_window(target)`
- `select_scene_object(instance_id=None, name=None, path=None)`
- `select_asset(asset_path=None, guid=None)`
- `open_asset(asset_path=None, guid=None)`

### Wait And Verify

- `wait_for_object(instance_id=None, name=None, path=None, exists=True, timeout_ms=5000, poll_ms=100)`
- `wait_for_log(text, log_type=None, since_seconds=60.0, timeout_ms=5000, poll_ms=100)`
- `wait_for_scene(scene_name=None, scene_path=None, timeout_ms=5000, poll_ms=100)`
- `wait_for_component_field(component_type, property_path, expected, instance_id=None, name=None, path=None, component_index=0, comparison="equals", timeout_ms=5000, poll_ms=100)`
- `wait_for_play_mode(is_playing=True, is_paused=None, timeout_ms=10000, poll_ms=100)`

### Test Runner And Screenshots

- `invoke_menu_item(menu_path)`
- `run_unity_tests(mode="editmode", assembly_names=None, test_names=None, group_names=None)`
- `get_unity_test_status(run_id=None)`
- `wait_for_unity_tests(require_success=True, timeout_ms=60000, poll_ms=250)`
- `read_console(count=50)`
- `take_scene_screenshot(source="scene_view", include_image=False, max_resolution=512)`

## MCP Resources

- `unity://editor/state`
- `unity://scene/hierarchy`

## Examples

See:

- `examples\codex-mcp.example.json`
- `examples\codex-config.example.toml`
- `examples\unity-manifest-git.example.json`

## Known Limitations

- only supports a localhost bridge by default
- not a real-time video stream
- tested mainly on Windows with Codex-driven workflows
- Unity editor APIs and MCP tool shapes may still change before `1.0`

## Roadmap

The project now has a phase-based roadmap in [ROADMAP.md](ROADMAP.md).

The next major block is `Editor Ergonomics`:

- full-editor screenshots
- pane focus and window control refinement
- asset open/select helpers
- better console and session workflows

After that, the plan moves into:

- `graph-core`
- `shader-graph-tools`
- `visual-scripting-readonly`

## Design Notes

- Unity APIs must run on Unity's main thread, so the bridge queues work into `EditorApplication.update`.
- The HTTP bridge is local-only and should not be exposed publicly without authentication and tighter restrictions.
- The repository keeps `main` installable from Git URL, so new work should be verified against a live Unity project before merge.
