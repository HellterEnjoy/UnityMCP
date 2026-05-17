# Agent Instructions

Use Unity MCP as the primary source of truth for Unity tasks.

## Default Rule

When the task involves Unity, inspect with Unity MCP before writing speculative code or making
assumptions about the current scene, objects, components, runtime state, assets, or test status.

## Required Workflow

For Unity-related work, follow this loop whenever possible:

1. inspect
2. act
3. verify
4. recover or refine if needed

## Recommended Entry Points

Start with one or more of these:

- `get_unity_capabilities()`
- `unity_health()`
- `get_editor_state()`
- `get_scene_hierarchy()`

## Unity MCP Coverage

Unity MCP already exposes tools for:

- scene hierarchy inspection
- GameObject search
- GameObject and component inspection
- GameObject creation, duplication, deletion, and transform changes
- component add/remove
- runtime component field read/write
- live runtime field/property reads during Play Mode when values differ from serialized state
- safe multi-step scene editing with snapshot, diff, rollback, and batch execution
- asset search/select/open/reveal
- ScriptableObject asset creation and serialized field edits
- editor window focus and screenshots
- full editor screenshots
- console checkpoints and log reads
- wait/assert checks
- Play Mode control
- input simulation
- Unity Test Runner execution and status polling

## Safe Edit Mode

When a task needs multiple scene edits, prefer:

- `snapshot_scene()`
- `safe_batch(...)`
- `diff_scene(...)`

Do not split large scene edits into unrelated one-off operations if safe batching can be used.

## Verification Policy

Do not assume success just because a tool call returned.

Verify through one or more of:

- hierarchy state
- inspected component fields
- runtime field values
- console output
- wait tools
- screenshots
- test status

## Visual Policy

When the task depends on what Unity visually shows, use screenshots instead of assumptions.

Prefer:

- `take_editor_screenshot(...)`
- `take_full_editor_screenshot(...)`
- `take_scene_screenshot(...)`

## Runtime Policy

When debugging gameplay or interactive behavior:

- enter Play Mode through Unity MCP
- inspect runtime fields directly
- use waits instead of fixed assumptions about timing
- use console checkpoints for fresh logs
- use keyboard/mouse/UI input tools if interaction is needed

## If Tools Seem Missing

If a session claims Unity scene/object/component/test tools are unavailable:

1. call `get_unity_capabilities()`
2. check the registered MCP server config
3. verify the session actually received the Unity MCP tool list

The server exposes these capabilities; missing access in a session usually means a client-side tool
registration or discovery issue rather than a Unity MCP feature gap.
