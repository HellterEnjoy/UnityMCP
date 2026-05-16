# Development

## Local Package Development

Use this when you want Unity to consume the package directly from disk while you edit this repo.

1. Open your Unity project.
2. Open `Window > Package Manager`.
3. Click `+ > Add package from disk...`.
4. Select:

```text
<path-to-this-repo>\unity-package\Packages\com.codex.unity-mcp\package.json
```

## Manual Health Checks

The bridge listens on:

```text
http://127.0.0.1:8765
```

Quick manual checks:

```text
http://127.0.0.1:8765/health
http://127.0.0.1:8765/editor/state
http://127.0.0.1:8765/scene/hierarchy?includeInactive=true&maxNodes=100
```

## Branching And Merge Policy

The repository should keep `main` installable from Git URL for Unity users.

Practical rule:

- do feature work on dedicated branches
- verify against a live Unity project
- merge to `main` only after end-to-end validation

Suggested active phase after the current work:

- `graph-core`
- `shader-graph-tools`
- `visual-scripting-readonly`
