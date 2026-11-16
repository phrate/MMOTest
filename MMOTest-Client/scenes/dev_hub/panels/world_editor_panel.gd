extends DevPanel
## 2D top-down world editor — panel root.
##
## Composed of three widgets (WorldEditorToolbar / WorldEditorCanvas /
## WorldEditorNpcInfo) wired via signals. Panel owns:
##   - The current mode, paint tool (z + walkable), brush type, clipboard
##   - Selection state (tile + npc_id)
##   - Pending NPC create/delete lists (not sent to server until Save)
##   - The instance + type caches fed from network
##   - Ctrl+C / Ctrl+V clipboard shortcuts
##
## Interactions:
##   LMB (inspect)                → select NPC on the clicked tile
##   LMB (paint/erase)            → paint / erase, drag-supported (canvas internal)
##   LMB (brush + type armed)     → queue a pending NPC placement
##   RMB on pending placement     → cancel it
##   RMB on committed NPC         → queue for deletion
##   MMB drag                      → pan (canvas internal)
##   Wheel                         → zoom (canvas internal)
##   Ctrl+C                         → copy selected NPC's type
##   Ctrl+V                         → queue a pending placement at last-selected tile
##
## Nothing NPC-related persists until Save World.

var _toolbar: WorldEditorToolbar
var _canvas: WorldEditorCanvas
var _npc_info: WorldEditorNpcInfo

var _mode := WorldEditorCanvas.MODE_INSPECT
var _z_value := 100
var _walkable := true
var _brush_type_id := 0
var _clipboard_type_id := 0

var _selected_tile := WorldEditorCanvas.INVALID_TILE
var _selected_npc_id := 0

var _pending_creates: Array = []
var _pending_deletes: Array = []

var _instances: Dictionary = {}
var _types_by_id: Dictionary = {}
var _type_options: Array = []


func _ready() -> void:
	_build_ui()
	_connect_network()


func panel_opened() -> void:
	Network.send_dev_world_instance_list()
	Network.send_dev_npc_type_list()


# ---------- Network ----------

func _connect_network() -> void:
	Network.dev_world_instance_list_result.connect(_on_instance_list)
	Network.dev_world_save_result.connect(_on_save_result)
	Network.dev_npc_type_list_result.connect(_on_type_list)


func _on_type_list(ok: bool, items: Array) -> void:
	if not ok: return
	_type_options = items
	_types_by_id.clear()
	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		_types_by_id[int(it.get("id", 0))] = it
	if _toolbar:
		_toolbar.set_type_options(_type_options)


func _on_instance_list(ok: bool, items: Array) -> void:
	if not ok: return
	_instances.clear()
	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var id := int(it.get("id", 0))
		if id == 0: continue
		_instances[id] = it
	if _canvas:
		_canvas.set_instances(_instances)
	_refresh_info_panel()


func _on_save_result(ok: bool, _error: String) -> void:
	if _toolbar == null: return
	if ok:
		_pending_creates.clear()
		_pending_deletes.clear()
		_canvas.set_pending_creates(_pending_creates)
		_canvas.set_pending_deletes(_pending_deletes)
		_refresh_info_panel()
		_toolbar.set_save_state(Texts.get_text("ui.dev_hub.world_editor.save_saved"), false)
		await get_tree().create_timer(1.2).timeout
		if _toolbar and _toolbar.has_method("set_save_state"):
			_toolbar.set_save_state(Texts.get_text("ui.dev_hub.world_editor.save_button"), false)
	else:
		_toolbar.set_save_state(Texts.get_text("ui.dev_hub.world_editor.save_failed"), false)
		await get_tree().create_timer(1.8).timeout
		if _toolbar and _toolbar.has_method("set_save_state"):
			_toolbar.set_save_state(Texts.get_text("ui.dev_hub.world_editor.save_button"), false)


# ---------- UI ----------

func _build_ui() -> void:
	var s := DevStyle.instance
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", s.panel_separation)
	add_child(vbox)

	_toolbar = WorldEditorToolbar.new()
	_toolbar.mode_changed.connect(_on_mode_changed)
	_toolbar.z_changed.connect(_on_z_changed)
	_toolbar.walkable_changed.connect(_on_walkable_changed)
	_toolbar.brush_chosen.connect(_on_brush_chosen)
	_toolbar.brush_cleared.connect(_on_brush_cleared)
	_toolbar.save_pressed.connect(_on_save_pressed)
	vbox.add_child(_toolbar)
	_toolbar.set_type_options(_type_options)

	_canvas = WorldEditorCanvas.new()
	_canvas.left_clicked.connect(_on_canvas_left_clicked)
	_canvas.right_clicked.connect(_on_canvas_right_clicked)
	_canvas.set_mode(_mode)
	_canvas.set_paint_tool(_z_value, _walkable)
	vbox.add_child(_canvas)

	_npc_info = WorldEditorNpcInfo.new()
	_npc_info.clipboard_clear_pressed.connect(_on_clear_clipboard)
	_canvas.add_child(_npc_info)
	_refresh_info_panel()


# ---------- Toolbar → state ----------

func _on_mode_changed(mode: int) -> void:
	_mode = mode
	if _canvas:
		_canvas.set_mode(_mode)

func _on_z_changed(z: int) -> void:
	_z_value = z
	if _canvas:
		_canvas.set_paint_tool(_z_value, _walkable)

func _on_walkable_changed(walkable: bool) -> void:
	_walkable = walkable
	if _canvas:
		_canvas.set_paint_tool(_z_value, _walkable)

func _on_brush_chosen(type_id: int) -> void:
	_brush_type_id = type_id

func _on_brush_cleared() -> void:
	_brush_type_id = 0


# ---------- Canvas → state ----------

func _on_canvas_left_clicked(tile: Vector2i) -> void:
	_selected_tile = tile

	if _mode == WorldEditorCanvas.MODE_BRUSH and _brush_type_id != 0:
		var z := World.get_z(tile.x, tile.y)
		_pending_creates.append({"type_id": _brush_type_id, "x": tile.x, "y": tile.y, "z": z})
		_canvas.set_pending_creates(_pending_creates)
		return

	# Inspect (or brush without type): select committed NPC if present
	_selected_npc_id = _find_npc_at(tile)
	_canvas.set_selection(_selected_tile, _selected_npc_id)
	_refresh_info_panel()

func _on_canvas_right_clicked(tile: Vector2i) -> void:
	# Pending placement → cancel it
	for i in range(_pending_creates.size()):
		var pc: Dictionary = _pending_creates[i]
		if int(pc["x"]) == tile.x and int(pc["y"]) == tile.y:
			_pending_creates.remove_at(i)
			_canvas.set_pending_creates(_pending_creates)
			_refresh_info_panel()
			return

	# Committed NPC → queue for deletion
	for id in _instances:
		if id in _pending_deletes: continue
		var inst: Dictionary = _instances[id]
		if int(inst.get("x", -1)) == tile.x and int(inst.get("y", -1)) == tile.y:
			_pending_deletes.append(int(id))
			if _selected_npc_id == int(id):
				_selected_npc_id = 0
			_canvas.set_selection(_selected_tile, _selected_npc_id)
			_canvas.set_pending_deletes(_pending_deletes)
			_refresh_info_panel()
			return


func _find_npc_at(tile: Vector2i) -> int:
	for id in _instances:
		if id in _pending_deletes: continue
		var inst: Dictionary = _instances[id]
		if int(inst.get("x", -1)) == tile.x and int(inst.get("y", -1)) == tile.y:
			return int(id)
	return 0


# ---------- Info panel ----------

func _refresh_info_panel() -> void:
	if _npc_info == null: return

	if _clipboard_type_id != 0:
		_npc_info.show_clipboard(_slug_for_type(_clipboard_type_id))
	else:
		_npc_info.hide_clipboard()

	if _selected_npc_id == 0 or not _instances.has(_selected_npc_id):
		_npc_info.show_no_selection()
		return

	var inst: Dictionary = _instances[_selected_npc_id].duplicate()
	inst["_deleting"] = _selected_npc_id in _pending_deletes
	_npc_info.show_selection(inst)


func _slug_for_type(type_id: int) -> String:
	var t: Dictionary = _types_by_id.get(type_id, {})
	return str(t.get("slug_id", "?"))


func _on_clear_clipboard() -> void:
	_clipboard_type_id = 0
	_refresh_info_panel()


# ---------- Ctrl+C / Ctrl+V ----------

func _unhandled_input(event: InputEvent) -> void:
	if not is_visible_in_tree(): return
	if not (event is InputEventKey and event.pressed and not event.echo): return
	if not event.ctrl_pressed: return

	if event.keycode == KEY_C:
		if _selected_npc_id != 0 and _instances.has(_selected_npc_id):
			var inst: Dictionary = _instances[_selected_npc_id]
			_clipboard_type_id = int(inst.get("type_id", 0))
			_refresh_info_panel()
			get_viewport().set_input_as_handled()
		return

	if event.keycode == KEY_V:
		if _clipboard_type_id != 0 and _selected_tile != WorldEditorCanvas.INVALID_TILE:
			var z := World.get_z(_selected_tile.x, _selected_tile.y)
			_pending_creates.append({
				"type_id": _clipboard_type_id,
				"x": _selected_tile.x,
				"y": _selected_tile.y,
				"z": z,
			})
			_canvas.set_pending_creates(_pending_creates)
			get_viewport().set_input_as_handled()
		return


# ---------- Save ----------

func _on_save_pressed() -> void:
	if not World.is_loaded: return
	_toolbar.set_save_state(Texts.get_text("ui.dev_hub.world_editor.save_saving"), true)
	var world_json := WorldEditorSerializer.build_world_json(
		World._tiles, World.world_version + 1)
	Network.send_dev_world_save(
		world_json, _pending_creates.duplicate(), _pending_deletes.duplicate())
