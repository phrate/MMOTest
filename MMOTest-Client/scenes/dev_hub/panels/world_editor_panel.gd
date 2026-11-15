extends DevPanel
## 2D top-down world editor.
##
## Layout:
##   Row 1: Inspect / Paint / Erase | Z + Walkable | NPC Brush + typeahead | spacer | [Save World]
##   Row 2: tile view (fills), with floating info panel top-right
##
## Interactions:
##   Left click tile (inspect)         → select NPC if present
##   Left click tile (paint/erase)     → paint or erase; click-hold-drag supported
##   Left click tile (brush armed)     → add pending NPC placement (committed on Save World)
##   Right click on committed NPC      → queue for deletion (committed on Save World)
##   Right click on pending NPC        → cancel pending placement
##   Ctrl+C                             → copy selected NPC's type to clipboard
##   Ctrl+V                             → add pending placement at selected tile
##   MMB drag                           → pan
##   Wheel                              → zoom (anchored at mouse)
##
## Nothing about NPC placement/deletion persists until Save World. Tile edits
## are already in-memory; Save World serializes both.

const TILE_PX := 1.0
const DEFAULT_ZOOM := 12.0
const ZOOM_MIN := 3.0
const ZOOM_MAX := 40.0
const ZOOM_STEP := 1.15
const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)

const TILE_WALK_A := Color(0.42, 0.58, 0.36)
const TILE_WALK_B := Color(0.36, 0.52, 0.30)
const TILE_BLOCKED := Color(0.65, 0.22, 0.22)
const TILE_HOVER := Color(1, 1, 1, 0.30)
const TILE_SELECTED_BORDER := Color(1.0, 0.82, 0.30, 1.0)
const NPC_MARKER := Color(0.30, 0.55, 1.0, 1.0)
const NPC_MARKER_SELECTED := Color(1.0, 0.82, 0.30, 1.0)
const NPC_MARKER_PENDING := Color(0.55, 0.85, 0.55, 1.0)
const NPC_MARKER_DELETING := Color(0.95, 0.35, 0.35, 1.0)

const MODE_INSPECT := 0
const MODE_PAINT := 1
const MODE_ERASE := 2
const MODE_BRUSH := 3

const MARKER_RADIUS_BASE := 3.0
const BORDER_WIDTH := 2.0
const INFO_PANEL_MARGIN := 12.0
const INFO_PANEL_WIDTH := 280.0
const SAVE_BTN_WIDTH := 200
const SAVE_BTN_HEIGHT := 48
const BRUSH_MAX_SUGGESTIONS := 8

var _camera_pos := Vector2.ZERO
var _zoom := DEFAULT_ZOOM
var _panning := false
var _pan_last := Vector2.ZERO

var _hovered_tile := INVALID_TILE
var _selected_tile := INVALID_TILE
var _selected_npc_id := 0

var _mode := MODE_INSPECT
var _z_value := 100
var _walkable := true

var _clipboard_type_id := 0
var _brush_type_id := 0

var _is_painting := false
var _last_painted_tile := INVALID_TILE

var _instances: Dictionary = {}
var _types_by_id: Dictionary = {}
var _type_options: Array = []

var _pending_creates: Array = []
var _pending_deletes: Array = []

var _tile_view: Control
var _info_panel: PanelContainer
var _info_title: RichTextLabel
var _info_body: RichTextLabel
var _clipboard_banner: PanelContainer
var _clipboard_slug_label: Label
var _clipboard_clear_btn: Button
var _mode_btn_group: Array = []
var _brush_toggle: Button
var _brush_input: LineEdit
var _brush_popup: PopupMenu
var _z_spin: SpinBox
var _walkable_toggle: Button
var _save_button: Button

var _style: DevStyle = null

func _ready() -> void:
	_style = DevStyle.instance
	_build_ui()
	_connect_network()

func panel_opened() -> void:
	Network.send_dev_world_instance_list()
	Network.send_dev_npc_type_list()

func panel_closed() -> void:
	pass

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

func _on_instance_list(ok: bool, items: Array) -> void:
	if not ok: return
	_instances.clear()
	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var id := int(it.get("id", 0))
		if id == 0: continue
		_instances[id] = it
	_tile_view.queue_redraw()

func _on_save_result(ok: bool, _error: String) -> void:
	if _save_button == null: return
	_save_button.disabled = false
	if ok:
		_pending_creates.clear()
		_pending_deletes.clear()
		_save_button.text = Texts.get_text("ui.dev_hub.world_editor.save_saved")
		await get_tree().create_timer(1.2).timeout
		if _save_button and _save_button.text == Texts.get_text("ui.dev_hub.world_editor.save_saved"):
			_save_button.text = Texts.get_text("ui.dev_hub.world_editor.save_button")
	else:
		_save_button.text = Texts.get_text("ui.dev_hub.world_editor.save_failed")
		await get_tree().create_timer(1.8).timeout
		if _save_button and _save_button.text == Texts.get_text("ui.dev_hub.world_editor.save_failed"):
			_save_button.text = Texts.get_text("ui.dev_hub.world_editor.save_button")

# ---------- UI construction ----------

func _build_ui() -> void:
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", _style.panel_separation)
	add_child(vbox)

	# Top bar
	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", _style.panel_separation)
	vbox.add_child(top)

	_mode_btn_group.append(_make_mode_button(Texts.get_text("ui.dev_hub.world_editor.mode_inspect"), MODE_INSPECT))
	_mode_btn_group.append(_make_mode_button(Texts.get_text("ui.dev_hub.world_editor.mode_paint"), MODE_PAINT))
	_mode_btn_group.append(_make_mode_button(Texts.get_text("ui.dev_hub.world_editor.mode_erase"), MODE_ERASE))
	for b in _mode_btn_group:
		top.add_child(b)
	_sync_mode_buttons()

	top.add_child(VSeparator.new())

	var z_lbl := Label.new()
	z_lbl.text = Texts.get_text("ui.dev_hub.world_editor.label_z")
	z_lbl.add_theme_font_size_override("font_size", _style.font_size_small)
	z_lbl.add_theme_color_override("font_color", _style.text_muted)
	z_lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	top.add_child(z_lbl)

	_z_spin = SpinBox.new()
	_z_spin.min_value = 0
	_z_spin.max_value = 999
	_z_spin.step = 1
	_z_spin.value = _z_value
	_z_spin.custom_minimum_size = Vector2(80, 0)
	_z_spin.value_changed.connect(func(v: float): _z_value = int(v))
	top.add_child(_z_spin)

	_walkable_toggle = Button.new()
	_walkable_toggle.toggle_mode = true
	_walkable_toggle.button_pressed = _walkable
	_walkable_toggle.focus_mode = Control.FOCUS_NONE
	_walkable_toggle.text = "[x] " + Texts.get_text("ui.dev_hub.world_editor.label_walkable")
	_walkable_toggle.add_theme_font_size_override("font_size", _style.font_size_small)
	_walkable_toggle.add_theme_color_override("font_color", _style.text_primary)
	_walkable_toggle.toggled.connect(_on_walkable_toggled)
	top.add_child(_walkable_toggle)

	top.add_child(VSeparator.new())

	_brush_toggle = Button.new()
	_brush_toggle.toggle_mode = true
	_brush_toggle.button_pressed = false
	_brush_toggle.focus_mode = Control.FOCUS_NONE
	_brush_toggle.text = Texts.get_text("ui.dev_hub.world_editor.brush_toggle")
	_brush_toggle.add_theme_font_size_override("font_size", _style.font_size_small)
	_brush_toggle.add_theme_color_override("font_color", _style.text_primary)
	_brush_toggle.toggled.connect(_on_brush_toggled)
	top.add_child(_brush_toggle)

	_brush_input = LineEdit.new()
	_brush_input.placeholder_text = Texts.get_text("ui.dev_hub.world_editor.brush_search_placeholder")
	_brush_input.custom_minimum_size = Vector2(200, 0)
	_brush_input.text_changed.connect(_on_brush_filter_changed)
	_brush_input.visible = false
	top.add_child(_brush_input)

	_brush_popup = PopupMenu.new()
	_brush_popup.add_theme_font_size_override("font_size", _style.font_size_small)
	_brush_popup.id_pressed.connect(_on_brush_suggestion_chosen)
	add_child(_brush_popup)

	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(spacer)

	_save_button = Button.new()
	_save_button.text = Texts.get_text("ui.dev_hub.world_editor.save_button")
	_save_button.focus_mode = Control.FOCUS_NONE
	_save_button.custom_minimum_size = Vector2(SAVE_BTN_WIDTH, SAVE_BTN_HEIGHT)
	_save_button.add_theme_font_size_override("font_size", 20)
	_save_button.add_theme_color_override("font_color", Color(1, 1, 1))
	_save_button.add_theme_color_override("font_hover_color", Color(1, 1, 1))
	_save_button.add_theme_color_override("font_pressed_color", Color(1, 1, 1))
	_save_button.add_theme_color_override("font_disabled_color", Color(0.75, 0.75, 0.75))
	var green := Color(0.28, 0.62, 0.28)
	_save_button.add_theme_stylebox_override("normal", _style.make_button_style(green))
	_save_button.add_theme_stylebox_override("hover", _style.make_button_style(green.lightened(0.12)))
	_save_button.add_theme_stylebox_override("pressed", _style.make_button_style(green.darkened(0.12)))
	_save_button.add_theme_stylebox_override("disabled", _style.make_button_style(green.darkened(0.35)))
	_save_button.pressed.connect(_on_save_pressed)
	top.add_child(_save_button)

	# Tile view
	_tile_view = Control.new()
	_tile_view.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_tile_view.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_tile_view.clip_contents = true
	_tile_view.mouse_filter = Control.MOUSE_FILTER_STOP
	_tile_view.gui_input.connect(_on_tile_view_input)
	_tile_view.draw.connect(_on_tile_view_draw)
	vbox.add_child(_tile_view)

	# Info panel (floating overlay inside tile view, top-right)
	_info_panel = PanelContainer.new()
	_info_panel.set_anchors_preset(Control.PRESET_TOP_RIGHT)
	_info_panel.offset_left = -INFO_PANEL_WIDTH - INFO_PANEL_MARGIN
	_info_panel.offset_right = -INFO_PANEL_MARGIN
	_info_panel.offset_top = INFO_PANEL_MARGIN
	_info_panel.offset_bottom = INFO_PANEL_MARGIN + 220
	_info_panel.mouse_filter = Control.MOUSE_FILTER_STOP

	var panel_style := _style.make_panel_style()
	panel_style.bg_color = Color(0.12, 0.12, 0.16, 0.94)
	panel_style.border_color = Color(0.35, 0.35, 0.42, 1.0)
	panel_style.content_margin_left = 12
	panel_style.content_margin_right = 12
	panel_style.content_margin_top = 12
	panel_style.content_margin_bottom = 12
	_info_panel.add_theme_stylebox_override("panel", panel_style)
	_tile_view.add_child(_info_panel)

	var info_vbox := VBoxContainer.new()
	info_vbox.add_theme_constant_override("separation", 8)
	_info_panel.add_child(info_vbox)

	# Clipboard banner (top of info panel when active)
	_clipboard_banner = PanelContainer.new()
	var cb_style := StyleBoxFlat.new()
	cb_style.bg_color = Color(0.22, 0.18, 0.05, 1.0)
	cb_style.border_color = Color(0.85, 0.65, 0.15, 1.0)
	cb_style.border_width_left = 1
	cb_style.border_width_right = 1
	cb_style.border_width_top = 1
	cb_style.border_width_bottom = 1
	cb_style.corner_radius_top_left = 4
	cb_style.corner_radius_top_right = 4
	cb_style.corner_radius_bottom_left = 4
	cb_style.corner_radius_bottom_right = 4
	cb_style.content_margin_left = 10
	cb_style.content_margin_right = 10
	cb_style.content_margin_top = 8
	cb_style.content_margin_bottom = 8
	_clipboard_banner.add_theme_stylebox_override("panel", cb_style)
	_clipboard_banner.visible = false
	info_vbox.add_child(_clipboard_banner)

	var cb_vbox := VBoxContainer.new()
	cb_vbox.add_theme_constant_override("separation", 4)
	_clipboard_banner.add_child(cb_vbox)

	var cb_title := Label.new()
	cb_title.text = Texts.get_text("ui.dev_hub.world_editor.clipboard_banner")
	cb_title.add_theme_font_size_override("font_size", _style.font_size_small)
	cb_title.add_theme_color_override("font_color", Color(0.85, 0.65, 0.15))
	cb_title.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.6))
	cb_title.add_theme_constant_override("outline_size", 2)
	cb_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	cb_vbox.add_child(cb_title)

	_clipboard_slug_label = Label.new()
	_clipboard_slug_label.text = ""
	_clipboard_slug_label.add_theme_font_size_override("font_size", 18)
	_clipboard_slug_label.add_theme_color_override("font_color", Color(1.0, 0.85, 0.30))
	_clipboard_slug_label.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.7))
	_clipboard_slug_label.add_theme_constant_override("outline_size", 3)
	_clipboard_slug_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	cb_vbox.add_child(_clipboard_slug_label)

	_clipboard_clear_btn = Button.new()
	_clipboard_clear_btn.text = Texts.get_text("ui.dev_hub.world_editor.clipboard_clear")
	_clipboard_clear_btn.focus_mode = Control.FOCUS_NONE
	_clipboard_clear_btn.add_theme_font_size_override("font_size", _style.font_size_small)
	_clipboard_clear_btn.add_theme_color_override("font_color", Color(1, 1, 1))
	_clipboard_clear_btn.add_theme_color_override("font_hover_color", Color(1, 1, 1))
	_clipboard_clear_btn.add_theme_color_override("font_pressed_color", Color(1, 1, 1))
	_clipboard_clear_btn.add_theme_stylebox_override("normal", _style.make_button_style(_style.button_close))
	_clipboard_clear_btn.add_theme_stylebox_override("hover", _style.make_button_style(_style.button_close.lightened(0.12)))
	_clipboard_clear_btn.add_theme_stylebox_override("pressed", _style.make_button_style(_style.button_close.darkened(0.12)))
	_clipboard_clear_btn.pressed.connect(_on_clear_clipboard)
	cb_vbox.add_child(_clipboard_clear_btn)

	# Title (heading) + body (details or hint)
	_info_title = RichTextLabel.new()
	_info_title.bbcode_enabled = true
	_info_title.fit_content = true
	_info_title.scroll_active = false
	_info_title.custom_minimum_size = Vector2(INFO_PANEL_WIDTH - 24, 30)
	_info_title.add_theme_font_size_override("normal_font_size", 18)
	info_vbox.add_child(_info_title)

	_info_body = RichTextLabel.new()
	_info_body.bbcode_enabled = true
	_info_body.fit_content = true
	_info_body.scroll_active = false
	_info_body.custom_minimum_size = Vector2(INFO_PANEL_WIDTH - 24, 60)
	_info_body.add_theme_font_size_override("normal_font_size", _style.font_size_small)
	info_vbox.add_child(_info_body)

	_refresh_info_panel()

# ---------- Top bar handlers ----------

func _make_mode_button(text: String, mode: int) -> Button:
	var b := Button.new()
	b.text = text
	b.toggle_mode = true
	b.focus_mode = Control.FOCUS_NONE
	b.add_theme_font_size_override("font_size", _style.font_size_small)
	b.add_theme_color_override("font_color", _style.text_primary)
	b.add_theme_color_override("font_hover_color", _style.text_primary)
	b.add_theme_color_override("font_pressed_color", _style.text_heading)
	b.pressed.connect(_on_mode_button_pressed.bind(mode))
	return b

func _on_mode_button_pressed(mode: int) -> void:
	if _brush_toggle.button_pressed:
		_brush_toggle.button_pressed = false
		_brush_input.visible = false
		_brush_popup.hide()
	_mode = mode
	_sync_mode_buttons()

func _sync_mode_buttons() -> void:
	for i in range(_mode_btn_group.size()):
		var b: Button = _mode_btn_group[i]
		b.set_pressed_no_signal(i == _mode)

func _on_walkable_toggled(pressed: bool) -> void:
	_walkable = pressed
	_walkable_toggle.text = ("[x] " if pressed else "[ ] ") + Texts.get_text("ui.dev_hub.world_editor.label_walkable")

func _on_brush_toggled(pressed: bool) -> void:
	_brush_input.visible = pressed
	if pressed:
		_mode = MODE_BRUSH
		_sync_mode_buttons()
		_brush_input.grab_focus()
	else:
		_mode = MODE_INSPECT
		_sync_mode_buttons()
		_brush_type_id = 0
		_brush_input.text = ""
		_brush_popup.hide()

func _on_brush_filter_changed(t: String) -> void:
	var filter := t.strip_edges().to_lower()
	if filter.is_empty():
		_brush_popup.hide()
		return
	_brush_popup.clear()
	var count := 0
	for it in _type_options:
		if typeof(it) != TYPE_DICTIONARY: continue
		var slug := str(it.get("slug_id", ""))
		var disp := str(it.get("display", ""))
		if not (slug.to_lower().contains(filter) or disp.to_lower().contains(filter)):
			continue
		if count >= BRUSH_MAX_SUGGESTIONS: break
		_brush_popup.add_item("%s  (%s)" % [disp, slug], count)
		_brush_popup.set_item_metadata(count, int(it.get("id", 0)))
		count += 1
	if count > 0:
		var rect := _brush_input.get_global_rect()
		_brush_popup.position = Vector2i(int(rect.position.x), int(rect.position.y + rect.size.y + 2))
		_brush_popup.reset_size()
		_brush_popup.popup()
	else:
		_brush_popup.hide()

func _on_brush_suggestion_chosen(id: int) -> void:
	var type_id: int = int(_brush_popup.get_item_metadata(id))
	_brush_type_id = type_id
	_brush_popup.hide()
	for it in _type_options:
		if int(it.get("id", 0)) == type_id:
			_brush_input.text = str(it.get("slug_id", ""))
			break

# ---------- Tile view input ----------

func _on_tile_view_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_panning = mb.pressed
			_pan_last = mb.position
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_zoom_at_mouse(mb.position, ZOOM_STEP)
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_zoom_at_mouse(mb.position, 1.0 / ZOOM_STEP)
			return
		if mb.button_index == MOUSE_BUTTON_LEFT:
			if mb.pressed:
				if _mode == MODE_PAINT or _mode == MODE_ERASE:
					_is_painting = true
					_last_painted_tile = _mouse_to_tile(mb.position)
					_paint_or_erase(_last_painted_tile)
				else:
					_on_tile_clicked(_mouse_to_tile(mb.position))
			else:
				_is_painting = false
				_last_painted_tile = INVALID_TILE
			return
		if mb.button_index == MOUSE_BUTTON_RIGHT and mb.pressed:
			_on_tile_right_clicked(_mouse_to_tile(mb.position))
			return
	elif event is InputEventMouseMotion:
		var mm := event as InputEventMouseMotion
		if _panning:
			_camera_pos -= (mm.position - _pan_last) / _zoom
			_pan_last = mm.position
			_tile_view.queue_redraw()
			return
		var t := _mouse_to_tile(mm.position)
		_hovered_tile = t
		if _is_painting and t != _last_painted_tile:
			_last_painted_tile = t
			_paint_or_erase(t)
		_tile_view.queue_redraw()

func _paint_or_erase(tile: Vector2i) -> void:
	if _mode == MODE_PAINT:
		World._tiles[tile] = {"z": _z_value, "flags": {"walkable": _walkable}}
	elif _mode == MODE_ERASE:
		World._tiles.erase(tile)
	_tile_view.queue_redraw()

func _on_tile_clicked(tile: Vector2i) -> void:
	_selected_tile = tile

	if _mode == MODE_BRUSH and _brush_type_id != 0:
		var z := World.get_z(tile.x, tile.y)
		_pending_creates.append({"type_id": _brush_type_id, "x": tile.x, "y": tile.y, "z": z})
		_tile_view.queue_redraw()
		return

	# Inspect: select committed NPC if present
	var found_id := 0
	for id in _instances:
		if id in _pending_deletes: continue
		var inst: Dictionary = _instances[id]
		if int(inst.get("x", -1)) == tile.x and int(inst.get("y", -1)) == tile.y:
			found_id = int(id)
			break
	_selected_npc_id = found_id
	_refresh_info_panel()
	_tile_view.queue_redraw()

func _on_tile_right_clicked(tile: Vector2i) -> void:
	# Pending placement → cancel it
	for i in range(_pending_creates.size()):
		var pc: Dictionary = _pending_creates[i]
		if int(pc["x"]) == tile.x and int(pc["y"]) == tile.y:
			_pending_creates.remove_at(i)
			_refresh_info_panel()
			_tile_view.queue_redraw()
			return
	# Committed NPC → queue for deletion
	for id in _instances:
		if id in _pending_deletes: continue
		var inst: Dictionary = _instances[id]
		if int(inst.get("x", -1)) == tile.x and int(inst.get("y", -1)) == tile.y:
			_pending_deletes.append(int(id))
			if _selected_npc_id == int(id):
				_selected_npc_id = 0
			_refresh_info_panel()
			_tile_view.queue_redraw()
			return

# ---------- Coord conversion ----------

func _tile_view_size() -> Vector2:
	return _tile_view.size

func _world_to_screen(world: Vector2) -> Vector2:
	return (world - _camera_pos) * _zoom + _tile_view_size() * 0.5

func _screen_to_world(screen: Vector2) -> Vector2:
	return (screen - _tile_view_size() * 0.5) / _zoom + _camera_pos

func _mouse_to_tile(screen: Vector2) -> Vector2i:
	var w := _screen_to_world(screen)
	return Vector2i(roundi(w.x / TILE_PX), roundi(w.y / TILE_PX))

func _zoom_at_mouse(mouse: Vector2, factor: float) -> void:
	var before := _screen_to_world(mouse)
	_zoom = clampf(_zoom * factor, ZOOM_MIN, ZOOM_MAX)
	var after := _screen_to_world(mouse)
	_camera_pos += before - after
	_tile_view.queue_redraw()

# ---------- Rendering ----------

func _tile_color(z: int, walkable: bool, alt: bool) -> Color:
	if not walkable: return TILE_BLOCKED
	var base := TILE_WALK_A if alt else TILE_WALK_B
	# Brightness ramps from 0.35 (z = default - 20) to 1.0 (z = default + 20).
	var t := clampf((float(z) - (float(World.default_z) - 20.0)) / 40.0, 0.0, 1.0)
	var factor := 0.35 + t * 0.65
	return base.darkened(1.0 - factor)

func _on_tile_view_draw() -> void:
	if not World.is_loaded: return
	var view_size := _tile_view_size()
	if view_size.x <= 0 or view_size.y <= 0: return

	var half_w := view_size.x * 0.5 / _zoom
	var half_h := view_size.y * 0.5 / _zoom
	var min_world := _camera_pos - Vector2(half_w, half_h)
	var max_world := _camera_pos + Vector2(half_w, half_h)
	var min_tile := Vector2i(floori(min_world.x / TILE_PX) - 1, floori(min_world.y / TILE_PX) - 1)
	var max_tile := Vector2i(ceili(max_world.x / TILE_PX) + 1, ceili(max_world.y / TILE_PX) + 1)
	var tile_px_screen := TILE_PX * _zoom

	for x in range(min_tile.x, max_tile.x + 1):
		for y in range(min_tile.y, max_tile.y + 1):
			var key := Vector2i(x, y)
			if not World._tiles.has(key): continue
			var t: Dictionary = World._tiles[key]
			var walkable: bool = bool(t.get("flags", {}).get("walkable", true))
			var z: int = int(t.get("z", World.default_z))
			var color := _tile_color(z, walkable, (x + y) % 2 == 0)
			var screen := _world_to_screen(Vector2(x * TILE_PX, y * TILE_PX))
			var rect := Rect2(screen - Vector2(tile_px_screen, tile_px_screen) * 0.5,
				Vector2(tile_px_screen, tile_px_screen))
			_tile_view.draw_rect(rect, color, true)

	if _hovered_tile != INVALID_TILE:
		var hs := _world_to_screen(Vector2(_hovered_tile.x * TILE_PX, _hovered_tile.y * TILE_PX))
		var hrect := Rect2(hs - Vector2(tile_px_screen, tile_px_screen) * 0.5,
			Vector2(tile_px_screen, tile_px_screen))
		_tile_view.draw_rect(hrect, TILE_HOVER, true)

	if _selected_tile != INVALID_TILE:
		var ss := _world_to_screen(Vector2(_selected_tile.x * TILE_PX, _selected_tile.y * TILE_PX))
		var srect := Rect2(ss - Vector2(tile_px_screen, tile_px_screen) * 0.5,
			Vector2(tile_px_screen, tile_px_screen))
		_tile_view.draw_rect(srect, TILE_SELECTED_BORDER, false, BORDER_WIDTH)

	# Committed NPCs
	var marker_radius := maxf(MARKER_RADIUS_BASE, _zoom * 0.35)
	for id in _instances:
		var inst: Dictionary = _instances[id]
		var wx := int(inst.get("x", 0))
		var wy := int(inst.get("y", 0))
		if wx < min_tile.x or wx > max_tile.x or wy < min_tile.y or wy > max_tile.y: continue
		var pos := _world_to_screen(Vector2(wx * TILE_PX, wy * TILE_PX))
		var c: Color
		if id in _pending_deletes:
			c = NPC_MARKER_DELETING
		elif int(id) == _selected_npc_id:
			c = NPC_MARKER_SELECTED
		else:
			c = NPC_MARKER
		_tile_view.draw_circle(pos, marker_radius, c)

	# Pending creates
	for pc in _pending_creates:
		var wx := int(pc["x"])
		var wy := int(pc["y"])
		if wx < min_tile.x or wx > max_tile.x or wy < min_tile.y or wy > max_tile.y: continue
		var pos := _world_to_screen(Vector2(wx * TILE_PX, wy * TILE_PX))
		_tile_view.draw_circle(pos, marker_radius, NPC_MARKER_PENDING)

# ---------- Info panel ----------

func _slug_for_type(type_id: int) -> String:
	var t: Dictionary = _types_by_id.get(type_id, {})
	return str(t.get("slug_id", "?"))

func _refresh_info_panel() -> void:
	if _info_body == null: return

	# Clipboard banner
	if _clipboard_type_id != 0:
		_clipboard_banner.visible = true
		_clipboard_slug_label.text = _slug_for_type(_clipboard_type_id)
	else:
		_clipboard_banner.visible = false

	# Title + body
	if _selected_npc_id == 0 or not _instances.has(_selected_npc_id):
		_info_title.text = ""
		_info_body.text = "[i][color=#9aa0a8]%s[/color][/i]" % Texts.get_text("ui.dev_hub.world_editor.no_selection")
		return

	var inst: Dictionary = _instances[_selected_npc_id]
	var slug := str(inst.get("slug_id", ""))
	var disp := str(inst.get("display", ""))
	var render := str(inst.get("render_slug", ""))
	var tx := int(inst.get("x", 0))
	var ty := int(inst.get("y", 0))
	var deleting := _selected_npc_id in _pending_deletes

	_info_title.text = "[b][color=#ffd84a][font_size=22]%s[/font_size][/color][/b]" % disp
	_info_body.text = (
		"[color=#8a8f96]%s[/color]\n\n"
		+ "[color=#8a8f96]%s[/color]  [color=#ffffff]%s[/color]\n"
		+ "[color=#8a8f96]%s[/color]  [color=#ffffff]%d, %d[/color]"
		+ ("\n\n[color=#ff6060][b]Queued for deletion[/b][/color]" if deleting else "")
	) % [
		slug,
		Texts.get_text("ui.dev_hub.world_editor.npc_info_render"), render,
		Texts.get_text("ui.dev_hub.world_editor.npc_info_tile"), tx, ty,
	]

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
		if _clipboard_type_id != 0 and _selected_tile != INVALID_TILE:
			var z := World.get_z(_selected_tile.x, _selected_tile.y)
			_pending_creates.append({
				"type_id": _clipboard_type_id,
				"x": _selected_tile.x,
				"y": _selected_tile.y,
				"z": z,
			})
			_tile_view.queue_redraw()
			get_viewport().set_input_as_handled()
		return

# ---------- Save ----------

func _on_save_pressed() -> void:
	if not World.is_loaded: return
	_save_button.disabled = true
	_save_button.text = Texts.get_text("ui.dev_hub.world_editor.save_saving")
	var world_json := _serialize_world()
	Network.send_dev_world_save(world_json, _pending_creates.duplicate(), _pending_deletes.duplicate())

func _serialize_world() -> Dictionary:
	var chunk_map := {}
	for key in World._tiles:
		var x: int = key.x
		var y: int = key.y
		var cx := floori(float(x) / World.chunk_size)
		var cy := floori(float(y) / World.chunk_size)
		var ck := "%d,%d" % [cx, cy]
		if not chunk_map.has(ck): chunk_map[ck] = []
		var d: Dictionary = World._tiles[key]
		var flags: Dictionary = d.get("flags", {})
		chunk_map[ck].append({
			"x": x, "y": y, "z": int(d.get("z", 100)),
			"flags": {"walkable": bool(flags.get("walkable", true))}
		})
	for ck in chunk_map:
		chunk_map[ck].sort_custom(_tile_less)
	var sorted_keys := chunk_map.keys()
	sorted_keys.sort()
	var chunks := {}
	for k in sorted_keys:
		chunks[k] = chunk_map[k]
	return {
		"format_version": World.FORMAT_VERSION,
		"world_version": World.world_version + 1,
		"chunk_size": World.chunk_size,
		"default_z": World.default_z,
		"z_min": World.z_min,
		"z_max": World.z_max,
		"chunks": chunks
	}

func _tile_less(a: Dictionary, b: Dictionary) -> bool:
	if a["y"] != b["y"]: return a["y"] < b["y"]
	return a["x"] < b["x"]
