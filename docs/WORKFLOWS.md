# Core Workflows

## Safe Edit Mode

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

## Semi-Realtime Testing

The runtime testing loop is lightweight but already practical:

1. enter Play Mode
2. wait until Unity finishes reloading
3. send input to the Game view
4. inspect or change runtime values
5. wait for expected logs or state changes
6. exit Play Mode or run Unity tests

This is not a video stream. It is a reconnect-tolerant request loop that is fast enough for
step-by-step agent testing.

## Editor Ergonomics

The editor ergonomics layer is the bridge between structured API calls and visual editor work.

Current capabilities:

- focus common Unity windows
- select and ping scene objects
- select and open assets from the Project view
- capture editor pane screenshots for Project, Inspector, Hierarchy, Scene, and Game

This is the layer that prepares the repository for graph tooling and deeper editor-driven workflows.
