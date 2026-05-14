from __future__ import annotations

import json
from typing import Any

from mcp.server.fastmcp import FastMCP

from .unity_client import UnityClient


mcp = FastMCP("codex-unity-mcp")
client = UnityClient()


def pretty(payload: dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, indent=2)


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
