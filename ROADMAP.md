# Codex Unity MCP Roadmap

This roadmap is intentionally practical. The goal is not to chase feature count. The goal is to
turn the repository into a reliable Unity automation layer for Codex:

- safe scene editing
- semi-realtime gameplay testing
- editor-aware visual inspection
- graph-based tooling for shader and node workflows

## Product Direction

The project is moving toward an agent workflow like this:

1. Open or focus the relevant Unity context.
2. Inspect the editor or runtime state.
3. Make a bounded change.
4. Verify the result structurally and visually.
5. Recover safely if the change is wrong.

## Roadmap Status

| Phase | Status | Goal |
| --- | --- | --- |
| Foundation | Done | Scene inspection, editing, screenshots, console, safe edit mode |
| Gameplay Loop | Done | Play Mode control, runtime field access, input, waits, test runner |
| Editor Ergonomics | In Progress | Better editor screenshots, window focus, asset open/select, console workflow |
| Graph Core | Planned | Generic node-graph inspection and editing layer |
| Shader Graph Tools | Planned | Open, inspect, edit, connect, and verify Shader Graph assets |
| Visual Scripting | Later | Read-first support, then safe graph edits |
| Advanced QA | Later | Visual regression, profiler assertions, richer runtime diagnostics |

## Phase 1: Foundation

Already implemented:

- Unity bridge health and editor state
- hierarchy search and object inspection
- create, delete, duplicate, transform, add/remove component
- screenshot capture from Scene View and Main Camera
- safe edit mode with snapshot, batch, rollback, and diff

## Phase 2: Gameplay Loop

Already implemented:

- enter and exit Play Mode
- Play Mode polling across bridge restarts
- runtime serialized field reads and writes
- Game view keyboard and mouse input
- wait helpers for objects, logs, scenes, fields, and Play Mode
- Unity Test Runner launch and polling

## Phase 3: Editor Ergonomics

This is the next development block. It should make the editor itself easier for Codex to drive.

Planned work:

- full Unity Editor screenshots
- screenshots of specific panes such as Hierarchy, Inspector, Console, Game, Scene, and Project
- open and focus specific windows or tabs
- select scene objects and project assets explicitly
- open assets by path and reveal them in Project view
- clear console and wait for logs since a checkpoint
- editor session checkpoints for selected object, focused pane, and open context
- step-by-step screenshot traces for agent runs

Implemented in this phase so far:

- screenshots of specific panes such as Hierarchy, Inspector, Project, Scene, and Game
- open and focus common editor windows
- select scene objects explicitly
- select and open assets from the Project view

Why this phase matters:

- graph tools will be brittle without better window and focus control
- screenshot-driven editor reasoning needs the whole editor, not only Scene and Game cameras
- QA loops become easier to debug when the editor state is visible and restorable

## Phase 4: Graph Core

This phase introduces a reusable graph abstraction instead of hard-coding one node editor at a time.

Planned work:

- open a graph asset
- list nodes, edges, groups, and comments
- find nodes by id, title, type, or property
- create and delete nodes
- connect and disconnect ports
- move nodes and normalize layout
- capture graph screenshots

Design rule:

Build a generic graph layer first, then add adapter layers for specific Unity graph editors.

## Phase 5: Shader Graph Tools

Shader Graph should be the first graph-specific implementation.

Planned work:

- open Shader Graph assets directly
- inspect graph structure and properties
- create common nodes
- edit node parameters
- connect ports and remove bad links
- inspect Blackboard properties and keywords
- screenshot the graph window for visual verification

Why this comes before visual scripting:

- it is a common real production workflow
- the graph model is constrained enough to automate safely
- it helps validate the graph-core API

## Phase 6: Visual Scripting

Visual scripting support should begin as read-first tooling.

Planned work:

- open visual scripting graph assets
- inspect flow and state graphs
- list nodes and connections
- identify events, branches, variables, and method calls
- screenshot graph editors

Safe editing comes later:

- create simple nodes
- connect known-safe ports
- validate graph integrity before saving

## Phase 7: Advanced QA

Longer-term testing and verification work:

- full-editor screenshot diffing
- baseline visual regression runs
- richer UI targeting than center-point clicks
- profiler counters and performance assertions
- animation, state machine, and physics assertions
- scenario macros for repeated test flows

## Branching Guidance

Suggested feature branches for the next stages:

- `editor-ergonomics`
- `graph-core`
- `shader-graph-tools`
- `visual-scripting-readonly`
- `advanced-qa`

## Merge Policy

The repository should keep `main` stable enough for Git URL installation in Unity.

Practical rule:

- branch for feature work
- verify against a live Unity project
- merge to `main` only after the feature survives real editor usage
