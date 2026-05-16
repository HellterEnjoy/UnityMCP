# MCP Tools

## Scene And Editor

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

## Safe Edit

- `snapshot_scene(snapshot_id=None)`
- `diff_scene(before_snapshot_id, after_snapshot_id=None)`
- `safe_batch(commands, rollback_on_error=True, label="Codex MCP Safe Batch")`

## Gameplay And Runtime

- `enter_play_mode()`
- `exit_play_mode()`
- `get_play_state()`
- `get_component_field(component_type, property_path, instance_id=None, name=None, path=None, component_index=0)`
- `set_component_field(component_type, property_path, value, instance_id=None, name=None, path=None, component_index=0)`
- `send_keyboard_input(key, event_type="press", character=None)`
- `send_mouse_input(x, y, event_type="click", button=0)`
- `click_ui_element(instance_id=None, name=None, path=None, button=0)`

## Editor Ergonomics

- `take_editor_screenshot(target="active_window", include_image=False, max_resolution=1400)`
- `take_full_editor_screenshot(include_image=False, max_resolution=1800)`
- `focus_editor_window(target)`
- `select_scene_object(instance_id=None, name=None, path=None)`
- `select_asset(asset_path=None, guid=None)`
- `open_asset(asset_path=None, guid=None)`
- `reveal_asset(asset_path=None, guid=None)`
- `search_assets(filter="", in_folders=None, limit=50)`
- `save_editor_session(session_id=None)`
- `restore_editor_session(session_id)`

## Wait And Verify

- `wait_for_object(instance_id=None, name=None, path=None, exists=True, timeout_ms=5000, poll_ms=100)`
- `wait_for_log(text, log_type=None, since_seconds=60.0, timeout_ms=5000, poll_ms=100)`
- `wait_for_scene(scene_name=None, scene_path=None, timeout_ms=5000, poll_ms=100)`
- `wait_for_component_field(component_type, property_path, expected, instance_id=None, name=None, path=None, component_index=0, comparison="equals", timeout_ms=5000, poll_ms=100)`
- `wait_for_play_mode(is_playing=True, is_paused=None, timeout_ms=10000, poll_ms=100)`

## Test Runner And Screenshots

- `invoke_menu_item(menu_path)`
- `run_unity_tests(mode="editmode", assembly_names=None, test_names=None, group_names=None)`
- `get_unity_test_status(run_id=None)`
- `wait_for_unity_tests(require_success=True, timeout_ms=60000, poll_ms=250)`
- `create_console_checkpoint(checkpoint_id=None)`
- `read_console_since_checkpoint(checkpoint_id)`
- `clear_console()`
- `read_console(count=50)`
- `take_scene_screenshot(source="scene_view", include_image=False, max_resolution=512)`

## MCP Resources

- `unity://editor/state`
- `unity://scene/hierarchy`
