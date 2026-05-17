from __future__ import annotations

import json
import time
from typing import Any

from mcp.server.fastmcp import FastMCP

from .unity_client import UnityBridgeError, UnityClient


mcp = FastMCP("unity-mcp")
client = UnityClient()


def pretty(payload: dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, indent=2)


_BATCH_PARAM_ALIASES = {
    "instance_id": "id",
    "primitive_type": "primitiveType",
    "parent_instance_id": "parentId",
    "parent_id": "parentId",
    "parent_name": "parentName",
    "parent_path": "parentPath",
    "new_name": "newName",
    "component_type": "componentType",
    "allow_multiple": "allowMultiple",
    "remove_all": "removeAll",
}


def normalize_batch_commands(commands: list[dict[str, Any]]) -> list[dict[str, Any]]:
    normalized = []
    for command in commands:
        item = dict(command)
        params = item.get("params")
        if isinstance(params, dict):
            item["params"] = {
                _BATCH_PARAM_ALIASES.get(str(key), str(key)): value for key, value in params.items()
            }
        args = item.get("args")
        if isinstance(args, dict):
            item["args"] = {
                _BATCH_PARAM_ALIASES.get(str(key), str(key)): value for key, value in args.items()
            }
        normalized.append(item)
    return normalized


def component_target_params(
    *,
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    component_type: str | None = None,
    component_index: int | None = None,
    property_path: str | None = None,
) -> dict[str, Any]:
    return {
        "id": instance_id,
        "name": name,
        "path": path,
        "componentType": component_type,
        "componentIndex": component_index,
        "propertyPath": property_path,
    }


def poll_bridge(
    fetch: Any,
    predicate: Any,
    timeout_ms: int,
    poll_ms: int,
    reconnect_ok: bool = False,
) -> dict[str, Any]:
    deadline = time.monotonic() + max(0.001, timeout_ms / 1000.0)
    last_payload: dict[str, Any] | None = None
    last_error: str | None = None

    while time.monotonic() < deadline:
        try:
            payload = fetch()
            last_payload = payload
            if predicate(payload):
                return payload
        except UnityBridgeError as exc:
            last_error = str(exc)
            if not reconnect_ok:
                raise

        time.sleep(max(0.01, poll_ms / 1000.0))

    if last_payload is not None:
        return {
            "ok": False,
            "error": "wait_timeout",
            "message": "Timed out waiting for Unity state change",
            "last": last_payload,
        }

    return {
        "ok": False,
        "error": "wait_timeout",
        "message": last_error or "Timed out waiting for Unity state change",
    }


@mcp.resource("unity://editor/state")
def editor_state_resource() -> str:
    """Current Unity Editor state, active scene, and selection."""
    return pretty(client.get("/editor/state"))


@mcp.resource("unity://scene/hierarchy")
def scene_hierarchy_resource() -> str:
    """Active scene hierarchy with components, capped to avoid huge payloads."""
    return pretty(client.get("/scene/hierarchy", includeInactive=True, maxNodes=500))


@mcp.tool()
def unity_health() -> str:
    """Check whether the Unity bridge is reachable."""
    return pretty(client.get("/health"))


@mcp.tool()
def get_editor_state() -> str:
    """Return Unity version, play mode, compilation state, active scene, and selection."""
    return pretty(client.get("/editor/state"))


@mcp.tool()
def get_scene_hierarchy(include_inactive: bool = True, max_nodes: int = 500) -> str:
    """Return the active scene hierarchy.

    Args:
        include_inactive: Include inactive GameObjects.
        max_nodes: Maximum GameObjects to return before truncating.
    """
    return pretty(client.get("/scene/hierarchy", includeInactive=include_inactive, maxNodes=max_nodes))


@mcp.tool()
def find_gameobjects(
    query: str = "",
    mode: str = "name",
    include_inactive: bool = True,
    limit: int = 50,
) -> str:
    """Find GameObjects in the active scene.

    Args:
        query: Search text. Empty query returns the first objects.
        mode: One of name, path, tag, or component.
        include_inactive: Include inactive GameObjects.
        limit: Maximum number of matches.
    """
    return pretty(
        client.get(
            "/scene/find",
            query=query,
            mode=mode,
            includeInactive=include_inactive,
            limit=limit,
        )
    )


@mcp.tool()
def inspect_gameobject(
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    include_properties: bool = True,
) -> str:
    """Inspect one GameObject by instance id, exact name, or hierarchy path.

    Args:
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact GameObject name. Instance id or path is safer if names repeat.
        path: Hierarchy path such as Root/Player/Camera.
        include_properties: Include serialized component properties.
    """
    return pretty(
        client.get(
            "/scene/gameobject",
            id=instance_id,
            name=name,
            path=path,
            includeProperties=include_properties,
        )
    )


@mcp.tool()
def set_transform(
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    position: list[float] | None = None,
    rotation: list[float] | None = None,
    scale: list[float] | None = None,
) -> str:
    """Set world position, world Euler rotation, or local scale for a GameObject.

    Args:
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact GameObject name. Instance id or path is safer if names repeat.
        path: Hierarchy path such as Root/Player.
        position: Optional [x, y, z] world position.
        rotation: Optional [x, y, z] world Euler angles.
        scale: Optional [x, y, z] local scale.
    """
    return pretty(
        client.get(
            "/scene/set-transform",
            id=instance_id,
            name=name,
            path=path,
            position=position,
            rotation=rotation,
            scale=scale,
        )
    )


@mcp.tool()
def create_gameobject(
    name: str = "GameObject",
    primitive_type: str = "empty",
    parent_instance_id: int | None = None,
    parent_name: str | None = None,
    parent_path: str | None = None,
    position: list[float] | None = None,
    rotation: list[float] | None = None,
    scale: list[float] | None = None,
) -> str:
    """Create a GameObject in the active scene.

    Args:
        name: Name for the new GameObject.
        primitive_type: One of empty, cube, sphere, capsule, cylinder, plane, or quad.
        parent_instance_id: Optional parent Unity instance id.
        parent_name: Optional exact parent GameObject name.
        parent_path: Optional parent hierarchy path.
        position: Optional [x, y, z] world position.
        rotation: Optional [x, y, z] world Euler angles.
        scale: Optional [x, y, z] local scale.
    """
    return pretty(
        client.get(
            "/scene/create-gameobject",
            name=name,
            primitiveType=primitive_type,
            parentId=parent_instance_id,
            parentName=parent_name,
            parentPath=parent_path,
            position=position,
            rotation=rotation,
            scale=scale,
        )
    )


@mcp.tool()
def delete_gameobject(
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
) -> str:
    """Delete a GameObject from the active scene with Unity Undo support.

    Args:
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact GameObject name. Instance id or path is safer if names repeat.
        path: Hierarchy path such as Root/Player.
    """
    return pretty(client.get("/scene/delete-gameobject", id=instance_id, name=name, path=path))


@mcp.tool()
def duplicate_gameobject(
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    new_name: str | None = None,
    parent_instance_id: int | None = None,
    parent_name: str | None = None,
    parent_path: str | None = None,
    position: list[float] | None = None,
    rotation: list[float] | None = None,
    scale: list[float] | None = None,
) -> str:
    """Duplicate a GameObject in the active scene.

    Args:
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact source GameObject name.
        path: Source hierarchy path such as Root/Player.
        new_name: Optional name for the duplicate.
        parent_instance_id: Optional destination parent Unity instance id.
        parent_name: Optional exact destination parent GameObject name.
        parent_path: Optional destination parent hierarchy path.
        position: Optional [x, y, z] world position.
        rotation: Optional [x, y, z] world Euler angles.
        scale: Optional [x, y, z] local scale.
    """
    return pretty(
        client.get(
            "/scene/duplicate-gameobject",
            id=instance_id,
            name=name,
            path=path,
            newName=new_name,
            parentId=parent_instance_id,
            parentName=parent_name,
            parentPath=parent_path,
            position=position,
            rotation=rotation,
            scale=scale,
        )
    )


@mcp.tool()
def add_component(
    component_type: str,
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    allow_multiple: bool = False,
) -> str:
    """Add a component to a GameObject with Unity Undo support.

    Args:
        component_type: Component short name or full type name, such as Rigidbody or UnityEngine.Rigidbody.
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact GameObject name. Instance id or path is safer if names repeat.
        path: Hierarchy path such as Root/Player.
        allow_multiple: Add even when the GameObject already has a matching component.
    """
    return pretty(
        client.get(
            "/scene/add-component",
            id=instance_id,
            name=name,
            path=path,
            componentType=component_type,
            allowMultiple=allow_multiple,
        )
    )


@mcp.tool()
def remove_component(
    component_type: str,
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    remove_all: bool = False,
) -> str:
    """Remove a component from a GameObject with Unity Undo support.

    Args:
        component_type: Component short name or full type name, such as Rigidbody or UnityEngine.Rigidbody.
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact GameObject name. Instance id or path is safer if names repeat.
        path: Hierarchy path such as Root/Player.
        remove_all: Remove all matching components instead of the first one.
    """
    return pretty(
        client.get(
            "/scene/remove-component",
            id=instance_id,
            name=name,
            path=path,
            componentType=component_type,
            removeAll=remove_all,
        )
    )


@mcp.tool()
def snapshot_scene(snapshot_id: str | None = None) -> str:
    """Capture a named structural snapshot of the active scene for later diffing.

    Args:
        snapshot_id: Optional snapshot id. If omitted, Unity creates a timestamp id.
    """
    return pretty(client.get("/safe/snapshot", id=snapshot_id))


@mcp.tool()
def diff_scene(before_snapshot_id: str, after_snapshot_id: str | None = None) -> str:
    """Diff a previous scene snapshot against another snapshot or the current scene.

    Args:
        before_snapshot_id: Snapshot id captured by snapshot_scene.
        after_snapshot_id: Optional second snapshot id. If omitted, compares against current scene.
    """
    return pretty(client.get("/safe/diff", before=before_snapshot_id, after=after_snapshot_id))


@mcp.tool()
def safe_batch(
    commands: list[dict[str, Any]],
    rollback_on_error: bool = True,
    label: str = "Unity MCP Safe Batch",
) -> str:
    """Run a batch of scene-edit commands in one Unity Undo group with optional rollback.

    Supported command tool names:
    create_gameobject, delete_gameobject, duplicate_gameobject, set_transform,
    add_component, remove_component.

    Args:
        commands: List of {"tool": "...", "params": {...}} command objects.
        rollback_on_error: Revert the entire Undo group if any command fails.
        label: Unity Undo group label.
    """
    return pretty(
        client.get(
            "/safe/batch",
            commands=json.dumps(normalize_batch_commands(commands), ensure_ascii=False),
            rollbackOnError=rollback_on_error,
            label=label,
        )
    )


@mcp.tool()
def enter_play_mode() -> str:
    """Request Unity to enter Play Mode."""
    return pretty(client.get("/play/enter"))


@mcp.tool()
def exit_play_mode() -> str:
    """Request Unity to exit Play Mode."""
    return pretty(client.get("/play/exit"))


@mcp.tool()
def get_play_state() -> str:
    """Return the current Unity Play Mode state."""
    return pretty(client.get("/play/state"))


@mcp.tool()
def invoke_menu_item(menu_path: str) -> str:
    """Invoke a Unity editor menu item by its full path.

    Args:
        menu_path: For example Window/General/Test Runner.
    """
    return pretty(client.get("/menu/invoke", menuPath=menu_path))


@mcp.tool()
def run_unity_tests(
    mode: str = "editmode",
    assembly_names: list[str] | None = None,
    test_names: list[str] | None = None,
    group_names: list[str] | None = None,
) -> str:
    """Start a Unity Test Runner execution.

    Args:
        mode: editmode, playmode, or all.
        assembly_names: Optional assembly filters.
        test_names: Optional fully qualified test names.
        group_names: Optional category/group filters.
    """
    return pretty(
        client.get(
            "/tests/run",
            mode=mode,
            assemblyNames=assembly_names,
            testNames=test_names,
            groupNames=group_names,
        )
    )


@mcp.tool()
def get_unity_test_status(run_id: str | None = None) -> str:
    """Return the status of the current Unity test run."""
    return pretty(client.get("/tests/status", runId=run_id))


@mcp.tool()
def get_component_field(
    component_type: str,
    property_path: str,
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    component_index: int = 0,
) -> str:
    """Read one serialized field from a component.

    Args:
        component_type: Component short name or full type name.
        property_path: Serialized property path, such as m_Text or m_Value.
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact GameObject name.
        path: Hierarchy path such as Canvas/Button.
        component_index: Which matching component to use when several exist.
    """
    return pretty(
        client.get(
            "/runtime/component-field",
            **component_target_params(
                instance_id=instance_id,
                name=name,
                path=path,
                component_type=component_type,
                component_index=component_index,
                property_path=property_path,
            ),
        )
    )


@mcp.tool()
def set_component_field(
    component_type: str,
    property_path: str,
    value: Any,
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    component_index: int = 0,
) -> str:
    """Write one serialized field on a component.

    Args:
        component_type: Component short name or full type name.
        property_path: Serialized property path.
        value: JSON-compatible target value.
        instance_id: Unity instance id from hierarchy/find results.
        name: Exact GameObject name.
        path: Hierarchy path such as Canvas/Button.
        component_index: Which matching component to use when several exist.
    """
    return pretty(
        client.get(
            "/runtime/set-component-field",
            **component_target_params(
                instance_id=instance_id,
                name=name,
                path=path,
                component_type=component_type,
                component_index=component_index,
                property_path=property_path,
            ),
            valueJson=json.dumps(value, ensure_ascii=False),
        )
    )


@mcp.tool()
def send_keyboard_input(key: str, event_type: str = "press", character: str | None = None) -> str:
    """Send a keyboard event to the Unity Game view.

    Args:
        key: Unity KeyCode name, such as Space or A.
        event_type: press, down, or up.
        character: Optional character payload for text input.
    """
    return pretty(client.get("/input/keyboard", key=key, eventType=event_type, character=character))


@mcp.tool()
def send_mouse_input(x: float, y: float, event_type: str = "click", button: int = 0) -> str:
    """Send a mouse event to the Unity Game view.

    Args:
        x: Screen-space X coordinate in pixels.
        y: Screen-space Y coordinate in pixels from the bottom of the Game view.
        event_type: click, down, up, or move.
        button: Mouse button index.
    """
    return pretty(client.get("/input/mouse", x=x, y=y, eventType=event_type, button=button))


@mcp.tool()
def click_ui_element(
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    button: int = 0,
) -> str:
    """Click the center of a UI RectTransform in the Unity Game view."""
    return pretty(client.get("/input/click-ui", id=instance_id, name=name, path=path, button=button))


@mcp.tool()
def take_editor_screenshot(
    target: str = "active_window",
    include_image: bool = False,
    max_resolution: int = 1400,
) -> str:
    """Capture a screenshot of a Unity editor window or pane.

    Args:
        target: active_window, scene, game, inspector, hierarchy, project, or console.
        include_image: Include base64 PNG in the response.
        max_resolution: Clamp the longest side of the capture.
    """
    return pretty(
        client.get(
            "/editor/screenshot",
            target=target,
            includeImage=include_image,
            maxResolution=max_resolution,
        )
    )


@mcp.tool()
def take_full_editor_screenshot(include_image: bool = False, max_resolution: int = 1800) -> str:
    """Capture the full Unity editor window."""
    return pretty(
        client.get(
            "/editor/full-screenshot",
            includeImage=include_image,
            maxResolution=max_resolution,
        )
    )


@mcp.tool()
def focus_editor_window(target: str) -> str:
    """Open or focus a Unity editor window.

    Args:
        target: scene, game, inspector, hierarchy, project, or console.
    """
    return pretty(client.get("/editor/focus-window", target=target))


@mcp.tool()
def select_scene_object(
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
) -> str:
    """Select and ping a scene object in the Unity editor."""
    return pretty(client.get("/editor/select-object", id=instance_id, name=name, path=path))


@mcp.tool()
def select_asset(asset_path: str | None = None, guid: str | None = None) -> str:
    """Select and ping an asset in the Unity editor."""
    return pretty(client.get("/editor/select-asset", assetPath=asset_path, guid=guid))


@mcp.tool()
def open_asset(asset_path: str | None = None, guid: str | None = None) -> str:
    """Open an asset in the Unity editor."""
    return pretty(client.get("/editor/open-asset", assetPath=asset_path, guid=guid))


@mcp.tool()
def reveal_asset(asset_path: str | None = None, guid: str | None = None) -> str:
    """Reveal an asset in the Unity Project window."""
    return pretty(client.get("/editor/reveal-asset", assetPath=asset_path, guid=guid))


@mcp.tool()
def search_assets(filter: str = "", in_folders: list[str] | None = None, limit: int = 50) -> str:
    """Search Unity assets through AssetDatabase.

    Args:
        filter: Unity AssetDatabase search string such as `t:Scene Sample`.
        in_folders: Optional list of folders like `Assets/Scenes`.
        limit: Maximum number of results to return.
    """
    return pretty(client.get("/editor/search-assets", filter=filter, inFolders=in_folders, limit=limit))


@mcp.tool()
def save_editor_session(session_id: str | None = None) -> str:
    """Save the current focused window and selection state."""
    return pretty(client.get("/editor/save-session", id=session_id))


@mcp.tool()
def restore_editor_session(session_id: str) -> str:
    """Restore a previously saved focused window and selection state."""
    return pretty(client.get("/editor/restore-session", id=session_id))


@mcp.tool()
def create_console_checkpoint(checkpoint_id: str | None = None) -> str:
    """Save the current Unity console position as a checkpoint."""
    return pretty(client.get("/console/checkpoint", id=checkpoint_id))


@mcp.tool()
def read_console_since_checkpoint(checkpoint_id: str) -> str:
    """Read Unity console entries created after a saved checkpoint."""
    return pretty(client.get("/console/since", id=checkpoint_id))


@mcp.tool()
def clear_console() -> str:
    """Clear the Unity Console."""
    return pretty(client.get("/console/clear"))


@mcp.tool()
def wait_for_object(
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    exists: bool = True,
    timeout_ms: int = 5000,
    poll_ms: int = 100,
) -> str:
    """Wait for a scene object to appear or disappear."""
    return pretty(
        client.get(
            "/wait/object",
            id=instance_id,
            name=name,
            path=path,
            exists=exists,
            timeoutMs=timeout_ms,
            pollMs=poll_ms,
        )
    )


@mcp.tool()
def wait_for_log(
    text: str,
    log_type: str | None = None,
    since_seconds: float = 60.0,
    timeout_ms: int = 5000,
    poll_ms: int = 100,
) -> str:
    """Wait for a Unity log entry containing the given text."""
    return pretty(
        client.get(
            "/wait/log",
            text=text,
            type=log_type,
            sinceSeconds=since_seconds,
            timeoutMs=timeout_ms,
            pollMs=poll_ms,
        )
    )


@mcp.tool()
def wait_for_scene(
    scene_name: str | None = None,
    scene_path: str | None = None,
    timeout_ms: int = 5000,
    poll_ms: int = 100,
) -> str:
    """Wait for the active scene to match the requested name or path."""
    return pretty(
        client.get(
            "/wait/scene",
            sceneName=scene_name,
            scenePath=scene_path,
            timeoutMs=timeout_ms,
            pollMs=poll_ms,
        )
    )


@mcp.tool()
def wait_for_component_field(
    component_type: str,
    property_path: str,
    expected: Any,
    instance_id: int | None = None,
    name: str | None = None,
    path: str | None = None,
    component_index: int = 0,
    comparison: str = "equals",
    timeout_ms: int = 5000,
    poll_ms: int = 100,
) -> str:
    """Wait until a component field matches an expected value."""
    return pretty(
        client.get(
            "/wait/component-field",
            **component_target_params(
                instance_id=instance_id,
                name=name,
                path=path,
                component_type=component_type,
                component_index=component_index,
                property_path=property_path,
            ),
            expectedJson=json.dumps(expected, ensure_ascii=False),
            comparison=comparison,
            timeoutMs=timeout_ms,
            pollMs=poll_ms,
        )
    )


@mcp.tool()
def wait_for_play_mode(
    is_playing: bool = True,
    is_paused: bool | None = None,
    timeout_ms: int = 10000,
    poll_ms: int = 100,
) -> str:
    """Wait for Unity Play Mode to reach the requested state."""
    payload = poll_bridge(
        lambda: client.get("/play/state"),
        lambda result: result.get("ok")
        and result.get("data", {}).get("isPlaying") == is_playing
        and (is_paused is None or result.get("data", {}).get("isPaused") == is_paused),
        timeout_ms=timeout_ms,
        poll_ms=poll_ms,
        reconnect_ok=True,
    )
    return pretty(payload)


@mcp.tool()
def wait_for_unity_tests(require_success: bool = True, timeout_ms: int = 60000, poll_ms: int = 250) -> str:
    """Wait for the current Unity test run to finish."""
    payload = poll_bridge(
        lambda: client.get("/tests/status"),
        lambda result: result.get("ok")
        and result.get("data", {}).get("status") != "running"
        and (not require_success or result.get("data", {}).get("failedCount", 0) == 0),
        timeout_ms=timeout_ms,
        poll_ms=poll_ms,
        reconnect_ok=True,
    )
    return pretty(payload)


@mcp.tool()
def read_console(count: int = 50) -> str:
    """Return recent Unity Console entries."""
    return pretty(client.get("/console", count=count))


@mcp.tool()
def take_scene_screenshot(
    source: str = "scene_view",
    include_image: bool = False,
    max_resolution: int = 512,
) -> str:
    """Capture a Unity camera screenshot.

    Args:
        source: scene_view or main_camera.
        include_image: Include base64 PNG in the response. Usually keep false unless visual analysis is needed.
        max_resolution: Width in pixels, clamped by the Unity bridge.
    """
    return pretty(
        client.get(
            "/screenshot",
            source=source,
            includeImage=include_image,
            maxResolution=max_resolution,
        )
    )


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
