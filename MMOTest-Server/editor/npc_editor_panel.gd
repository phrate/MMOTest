extends DevPanel
## NPC editor — client-side port of the server NpcEditor.
##
## Browse + preview + full field inspector + save.
## Piece 2c will add hitbox and scale authoring (visible next to the fields
## when those controls land).
##
## Two modes:
##   unique   — live NPCs (npcs table + their levels_combat instance)
##   template — combat definitions shared by many NPCs
##
## Note on tile position: changing tile_x/y/z saves to the DB immediately,
## but the live NPC keeps its current position until it respawns. Kill it
## via [Admin] Kill if you need the new position now.

const TARGET_HEIGHT_BY_SLUG := {
	"player": 1.8, "man": 1.8, "guard": 1.8,
	"goblin": 1.4, "chicken": 0.4,
}

@export var model_viewport_min_size: Vector2 = Vector2(400, 400)
@export var default_orbit_yaw: float = 0.6
@export var default_orbit_pitch: float = 0.35
@export var default_orbit_distance: float = 4.0
@export var orbit_mouse_sensitivity: float = 0.008
@export var orbit_zoom_step: float = 0.9
@export var orbit_min_distance: float = 0.5
@export var orbit_max_distance: float = 50.0
@export var orbit_pitch_clamp: float = 1.4
@export var left_column_ratio: float = 0.30
@export var field_label_width: int = 160
@export var field_row_separation: int = 8
@export var field_widget_min_height: int = 60

var _mode: String = "unique"

# Widgets
var _mode_selector: OptionButton
var _search_edit: LineEdit
var _npc_selector: OptionButton
var _preview_placeholder: Label
var _fields_container: VBoxContainer
var _save_button: Button

# SubViewport 3D
var _sub_viewport: SubViewport
var _sub_viewport_container: SubViewportContainer
var _camera: Camera3D
var _model_holder: Node3D
var _current_model: Node3D = null
var _current_slug: String = ""
var _current_data: Dictionary = {}

# List state
var _items: Array = []
var _filtered_items: Array = []
var _current_id: int = 0
var _list_loaded: bool = false
var _pending_npc_id: int = 0

# Field widgets keyed by field name
var _fields: Dictionary = {}

# Orbit state
var _orbit_yaw: float = 0.6
var _orbit_pitch: float = 0.35
var _orbit_distance: float = 4.0
var _orbit_target: Vector3 = Vector3(0, 1, 0)
var _orbiting: bool = false

var _style: DevStyle = null

func _ready() -> void:
	_style = DevStyle.instance
	_build_ui()
	_connect_network()
	_orbit_yaw = default_orbit_yaw
	_orbit_pitch = default_orbit_pitch
	_orbit_distance = default_orbit_distance

# ---------- DevPanel hooks ----------

func panel_opened() -> void:
	if not _list_loaded:
		_reload_list()

func on_hub_open_for_entity(entity_type: String, entity_id: int) -> void:
	if entity_type != "npc":
		return
	if _mode != "unique":
		_set_mode("unique")
	_pending_npc_id = entity_id
	if _list_loaded:
		_try_select_pending()

# ---------- UI construction ----------

func _build_ui() -> void:
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", _style.panel_separation)
	add_child(vbox)

	# Row 1: mode + search + dropdown
	var filter_row := HBoxContainer.new()
	filter_row.add_theme_constant_override("separation", _style.panel_separation)
	vbox.add_child(filter_row)

	_mode_selector = OptionButton.new()
	_mode_selector.add_item(Texts.get_text("ui.dev_hub.npc_editor.mode_unique"), 0)
	_mode_selector.add_item(Texts.get_text("ui.dev_hub.npc_editor.mode_template"), 1)
	_mode_selector.select(0)
	_mode_selector.item_selected.connect(_on_mode_selected)
	filter_row.add_child(_mode_selector)

	_search_edit = LineEdit.new()
	_search_edit.placeholder_text = Texts.get_text("ui.dev_hub.npc_editor.search_placeholder")
	_search_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_search_edit.text_changed.connect(_on_search_changed)
	filter_row.add_child(_search_edit)

	_npc_selector = OptionButton.new()
	_npc_selector.custom_minimum_size = Vector2(280, 0)
	_npc_selector.item_selected.connect(_on_npc_selected)
	filter_row.add_child(_npc_selector)

	# Row 2: split — model | fields
	var split := HSplitContainer.new()
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vbox.add_child(split)

	# Left: model preview
	var left := VBoxContainer.new()
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	left.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.add_child(left)

	_sub_viewport_container = SubViewportContainer.new()
	_sub_viewport_container.custom_minimum_size = model_viewport_min_size
	_sub_viewport_container.stretch = true
	_sub_viewport_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_sub_viewport_container.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_sub_viewport_container.gui_input.connect(_on_viewport_input)
	left.add_child(_sub_viewport_container)

	_sub_viewport = SubViewport.new()
	_sub_viewport.own_world_3d = true
	_sub_viewport.handle_input_locally = false
	_sub_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_sub_viewport_container.add_child(_sub_viewport)

	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.15, 0.15, 0.18, 1.0)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(1, 1, 1)
	env.ambient_light_energy = 0.5
	var world_env := WorldEnvironment.new()
	world_env.environment = env
	_sub_viewport.add_child(world_env)

	_camera = Camera3D.new()
	_camera.current = true
	_sub_viewport.add_child(_camera)

	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-50, -30, 0)
	light.light_energy = 1.2
	light.shadow_enabled = true
	_sub_viewport.add_child(light)

	_model_holder = Node3D.new()
	_model_holder.name = "ModelHolder"
	_sub_viewport.add_child(_model_holder)

	_preview_placeholder = Label.new()
	_preview_placeholder.text = Texts.get_text("ui.dev_hub.npc_editor.preview_placeholder")
	_preview_placeholder.add_theme_font_size_override("font_size", _style.font_size_body)
	_preview_placeholder.add_theme_color_override("font_color", _style.text_muted)
	_preview_placeholder.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_preview_placeholder.set_anchors_and_offsets_preset(Control.PRESET_CENTER_TOP)
	_sub_viewport_container.add_child(_preview_placeholder)

	# Right: fields + save
	var right := VBoxContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right.add_theme_constant_override("separation", _style.panel_separation)
	split.add_child(right)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	right.add_child(scroll)

	_fields_container = VBoxContainer.new()
	_fields_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_fields_container.add_theme_constant_override("separation", 4)
	scroll.add_child(_fields_container)

	var save_row := HBoxContainer.new()
	right.add_child(save_row)

	_save_button = Button.new()
	_save_button.text = "Save"
	_save_button.disabled = true
	_save_button.focus_mode = Control.FOCUS_NONE
	_save_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_save_button.add_theme_font_size_override("font_size", _style.font_size_body)
	_save_button.add_theme_color_override("font_color", _style.text_primary)
	_save_button.add_theme_color_override("font_hover_color", _style.text_primary)
	_save_button.add_theme_color_override("font_pressed_color", _style.text_primary)
	_save_button.add_theme_color_override("font_disabled_color", _style.text_muted)
	_save_button.pressed.connect(_on_save_pressed)
	save_row.add_child(_save_button)

	split.split_offset = int(DevStyle.instance.panel_max_width * left_column_ratio)
	_update_camera_orbit()

# ---------- Network ----------

func _connect_network() -> void:
	Network.dev_npc_editor_list_result.connect(_on_list_result)
	Network.dev_npc_editor_template_data.connect(_on_template_data)
	Network.dev_npc_editor_template_saved.connect(_on_template_saved)
	Network.npc_editor_data.connect(_on_npc_editor_data)
	Network.npc_editor_saved.connect(_on_npc_editor_saved)

# ---------- List ----------

func _reload_list() -> void:
	Network.send_dev_npc_editor_list(_mode)

func _on_list_result(ok: bool, mode: String, items: Array) -> void:
	if mode != _mode: return
	if not ok:
		_items = []
		_filtered_items = []
		_npc_selector.clear()
		_list_loaded = true
		return
	_items = items
	_list_loaded = true
	_rebuild_dropdown()

func _rebuild_dropdown() -> void:
	_npc_selector.clear()
	_filtered_items.clear()
	var filter := _search_edit.text.strip_edges().to_lower()
	for it in _items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var label := str(it.get("name", it.get("slug_id", "")))
		if filter.is_empty() or label.to_lower().contains(filter):
			_filtered_items.append(it)
			_npc_selector.add_item(label)
	if _filtered_items.is_empty():
		_current_id = 0
		_current_slug = ""
		_clear_model()
		return
	var selected_index := -1
	for i in _filtered_items.size():
		if int(_filtered_items[i].id) == _current_id:
			selected_index = i
			break
	if selected_index < 0:
		selected_index = 0
		_current_id = int(_filtered_items[0].id)
	_npc_selector.select(selected_index)
	_load_current()

func _on_npc_selected(idx: int) -> void:
	if idx < 0 or idx >= _filtered_items.size(): return
	_current_id = int(_filtered_items[idx].id)
	_load_current()

func _on_search_changed(_t: String) -> void:
	_rebuild_dropdown()

func _on_mode_selected(idx: int) -> void:
	var new_mode := "unique" if idx == 0 else "template"
	if new_mode == _mode: return
	_set_mode(new_mode)

func _set_mode(new_mode: String) -> void:
	_mode = new_mode
	_current_id = 0
	_current_slug = ""
	_list_loaded = false
	_items = []
	_filtered_items = []
	_npc_selector.clear()
	_clear_model()
	_reload_list()

func _try_select_pending() -> void:
	if _pending_npc_id == 0: return
	for i in _filtered_items.size():
		if int(_filtered_items[i].id) == _pending_npc_id:
			_current_id = _pending_npc_id
			_npc_selector.select(i)
			_load_current()
			_pending_npc_id = 0
			return
	_pending_npc_id = 0

# ---------- Load current ----------

func _load_current() -> void:
	if _current_id == 0:
		_clear_model()
		return
	if _mode == "unique":
		Network.send_npc_editor_get(_current_id)
	else:
		Network.send_dev_npc_editor_get_template(_current_id)

func _on_npc_editor_data(npc: Dictionary) -> void:
	if _mode != "unique": return
	if int(npc.get("id", 0)) != _current_id: return
	_current_data = npc
	_load_model(str(npc.get("render_slug", "")))
	_build_inspector()

func _on_template_data(ok: bool, template: Dictionary) -> void:
	if _mode != "template": return
	if not ok: return
	if int(template.get("id", 0)) != _current_id: return
	_current_data = template
	var sid := str(template.get("slug_id", ""))
	_load_model(sid.trim_prefix("npc_"))
	_build_inspector()

# ---------- Inspector ----------

func _build_inspector() -> void:
	for c in _fields_container.get_children():
		_fields_container.remove_child(c)
		c.queue_free()
	_fields.clear()

	var specs := _unique_fields() if _mode == "unique" else _template_fields()
	for spec in specs:
		if str(spec.get("type", "")) == "separator":
			_fields_container.add_child(HSeparator.new())
			continue
		_add_field_row(spec)

	_save_button.disabled = false
	_save_button.text = "Save"

func _add_field_row(spec: Dictionary) -> void:
	var key: String = spec["key"]
	var label_text: String = spec["label"]
	var type: String = spec["type"]
	var value: Variant = _current_data.get(key)

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", field_row_separation)
	_fields_container.add_child(row)

	var lbl := Label.new()
	lbl.text = label_text
	lbl.custom_minimum_size = Vector2(field_label_width, 0)
	lbl.add_theme_font_size_override("font_size", _style.font_size_body)
	lbl.add_theme_color_override("font_color", _style.text_primary)
	row.add_child(lbl)

	match type:
		"readonly":
			var l := Label.new()
			l.text = str(value) if value != null else ""
			l.add_theme_font_size_override("font_size", _style.font_size_body)
			l.add_theme_color_override("font_color", _style.text_muted)
			l.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			row.add_child(l)
		"readonly_bool":
			var l := Label.new()
			l.text = "yes" if bool(value) else "no"
			l.add_theme_font_size_override("font_size", _style.font_size_body)
			l.add_theme_color_override("font_color", _style.text_muted)
			l.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			row.add_child(l)
		"string":
			var le := LineEdit.new()
			le.text = str(value) if value != null else ""
			le.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			row.add_child(le)
			_fields[key] = le
		"int":
			var sb := SpinBox.new()
			sb.min_value = int(spec.get("min", -999999))
			sb.max_value = 999999
			sb.step = 1
			sb.value = int(value) if value != null else 0
			sb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			row.add_child(sb)
			_fields[key] = sb
		"bool":
			var cb := CheckBox.new()
			cb.button_pressed = bool(value) if value != null else false
			row.add_child(cb)
			_fields[key] = cb
		"text":
			var te := TextEdit.new()
			te.text = str(value) if value != null else ""
			te.custom_minimum_size = Vector2(0, field_widget_min_height)
			te.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			row.add_child(te)
			_fields[key] = te

func _on_save_pressed() -> void:
	if _current_id == 0: return
	_save_button.disabled = true
	_save_button.text = "Saving..."

	var updates := {}
	for key in _fields.keys():
		var c = _fields[key]
		if c is SpinBox: updates[key] = int((c as SpinBox).value)
		elif c is LineEdit: updates[key] = (c as LineEdit).text
		elif c is CheckBox: updates[key] = (c as CheckBox).button_pressed
		elif c is TextEdit: updates[key] = (c as TextEdit).text

	if _mode == "unique":
		Network.send_npc_editor_save(_current_id, updates)
	else:
		Network.send_dev_npc_editor_save_template(_current_id, updates)

# ---------- Save callbacks ----------

func _on_npc_editor_saved(ok: bool, npc_id: int) -> void:
	if _mode != "unique": return
	if npc_id != _current_id: return
	if not _save_button: return
	_save_button.disabled = false
	if ok:
		_save_button.text = "Saved ✓"
		Network.send_npc_editor_get(_current_id)
		await get_tree().create_timer(1.2).timeout
		if _save_button and _save_button.text == "Saved ✓":
			_save_button.text = "Save"
	else:
		_save_button.text = "Save Failed"
		await get_tree().create_timer(2.0).timeout
		if _save_button and _save_button.text == "Save Failed":
			_save_button.text = "Save"

func _on_template_saved(ok: bool, template_id: int) -> void:
	if _mode != "template": return
	if template_id != _current_id: return
	if not _save_button: return
	_save_button.disabled = false
	if ok:
		_save_button.text = "Saved ✓"
		Network.send_dev_npc_editor_get_template(_current_id)
		await get_tree().create_timer(1.2).timeout
		if _save_button and _save_button.text == "Saved ✓":
			_save_button.text = "Save"
	else:
		_save_button.text = "Save Failed"
		await get_tree().create_timer(2.0).timeout
		if _save_button and _save_button.text == "Save Failed":
			_save_button.text = "Save"

# ---------- Model ----------

func _load_model(slug: String) -> void:
	if slug == "":
		_clear_model_only()
		return
	_clear_model_only()
	_current_slug = slug
	var scene := NpcRender.get_scene(slug)
	if scene == null:
		_clear_model_only()
		return
	var inst := scene.instantiate() as Node3D
	if inst == null:
		_clear_model_only()
		return
	_model_holder.add_child(inst)
	_current_model = inst
	_preview_placeholder.visible = false
	_apply_model_fit(slug)
	_frame_camera(inst)
	_update_camera_orbit()

func _apply_model_fit(slug: String) -> void:
	if _current_model == null: return
	var aabb := _measure_world_aabb(_current_model)
	if aabb.size.y < 0.001: return
	var target_h: float = TARGET_HEIGHT_BY_SLUG.get(slug, 1.8)
	var fit_factor := target_h / aabb.size.y
	_current_model.scale = _current_model.scale * fit_factor
	aabb = _measure_world_aabb(_current_model)
	_current_model.position.y -= aabb.position.y

func _measure_world_aabb(root: Node3D) -> AABB:
	var first := true
	var result := AABB()
	for mi in _find_mesh_instances(root):
		if mi.mesh == null: continue
		var world_box: AABB = mi.global_transform * mi.mesh.get_aabb()
		if first:
			result = world_box
			first = false
		else:
			result = result.merge(world_box)
	return result

func _find_mesh_instances(node: Node) -> Array:
	var out := []
	if node is MeshInstance3D:
		out.append(node)
	for c in node.get_children():
		out.append_array(_find_mesh_instances(c))
	return out

func _clear_model_only() -> void:
	if _current_model != null and is_instance_valid(_current_model):
		_current_model.queue_free()
	_current_model = null
	if _preview_placeholder:
		_preview_placeholder.visible = true

func _clear_model() -> void:
	_clear_model_only()
	_current_data = {}
	_current_slug = ""
	if _fields_container:
		for c in _fields_container.get_children():
			_fields_container.remove_child(c)
			c.queue_free()
	_fields.clear()
	if _save_button:
		_save_button.disabled = true
		_save_button.text = "Save"

# ---------- Camera ----------

func _frame_camera(root: Node3D) -> void:
	var aabb := _measure_world_aabb(root)
	if aabb.size == Vector3.ZERO:
		aabb = AABB(Vector3(-1, 0, -1), Vector3(2, 2, 2))
	_orbit_target = aabb.get_center()
	var size := maxf(aabb.size.x, maxf(aabb.size.y, aabb.size.z))
	_orbit_distance = maxf(size, 0.5) * 2.5

func _update_camera_orbit() -> void:
	if _camera == null: return
	var cp := cos(_orbit_pitch)
	var sp := sin(_orbit_pitch)
	var cy := cos(_orbit_yaw)
	var sy := sin(_orbit_yaw)
	var offset := Vector3(_orbit_distance * cp * sy, _orbit_distance * sp, _orbit_distance * cp * cy)
	_camera.global_position = _orbit_target + offset
	_camera.look_at(_orbit_target, Vector3.UP)

# ---------- Input ----------

func _on_viewport_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_orbiting = mb.pressed
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_orbit_distance = maxf(orbit_min_distance, _orbit_distance * orbit_zoom_step)
			_update_camera_orbit()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_orbit_distance = minf(orbit_max_distance, _orbit_distance / orbit_zoom_step)
			_update_camera_orbit()
			return
	elif event is InputEventMouseMotion and _orbiting:
		var mm := event as InputEventMouseMotion
		_orbit_yaw -= mm.relative.x * orbit_mouse_sensitivity
		_orbit_pitch = clampf(
			_orbit_pitch + mm.relative.y * orbit_mouse_sensitivity,
			-orbit_pitch_clamp, orbit_pitch_clamp)
		_update_camera_orbit()

# ---------- Field specs ----------

func _template_fields() -> Array:
	return [
		{"key": "slug_id", "label": "Slug ID", "type": "readonly"},
		{"key": "final_level", "label": "Final Level", "type": "int", "min": 1},
		{"key": "hitpoints", "label": "Hitpoints", "type": "int", "min": 0},
		{"key": "max_hitpoints", "label": "Max Hitpoints", "type": "int", "min": 1},
		{"key": "attack_speed", "label": "Attack Speed (ticks)", "type": "int", "min": 1},
		{"type": "separator"},
		{"key": "accuracy", "label": "Accuracy", "type": "int", "min": 1},
		{"key": "max_accuracy", "label": "Max Accuracy", "type": "int", "min": 1},
		{"key": "strength", "label": "Strength", "type": "int", "min": 1},
		{"key": "max_strength", "label": "Max Strength", "type": "int", "min": 1},
		{"key": "defence", "label": "Defence", "type": "int", "min": 1},
		{"key": "max_defence", "label": "Max Defence", "type": "int", "min": 1},
		{"key": "ether", "label": "Ether", "type": "int", "min": 1},
		{"key": "max_ether", "label": "Max Ether", "type": "int", "min": 1},
		{"type": "separator"},
		{"key": "melee_bonus", "label": "Melee Bonus", "type": "int", "min": 0},
		{"key": "ranged_bonus", "label": "Ranged Bonus", "type": "int", "min": 0},
		{"key": "ether_bonus", "label": "Ether Bonus", "type": "int", "min": 0},
		{"key": "melee_defense", "label": "Melee Defense", "type": "int", "min": 0},
		{"key": "ranged_defense", "label": "Ranged Defense", "type": "int", "min": 0},
		{"key": "ether_defense", "label": "Ether Defense", "type": "int", "min": 0},
	]

func _unique_fields() -> Array:
	return [
		{"key": "name", "label": "Name", "type": "readonly"},
		{"key": "display", "label": "Display", "type": "string"},
		{"key": "render_slug", "label": "Render Slug", "type": "string"},
		{"key": "dialogue_script", "label": "Dialogue Script", "type": "string"},
		{"key": "is_banker", "label": "Is Banker", "type": "bool"},
		{"key": "is_dead", "label": "Is Dead", "type": "readonly_bool"},
		{"type": "separator"},
		{"key": "tile_x", "label": "Tile X", "type": "int"},
		{"key": "tile_y", "label": "Tile Y", "type": "int"},
		{"key": "tile_z", "label": "Tile Z", "type": "int"},
		{"type": "separator"},
		{"key": "attackable", "label": "Attackable", "type": "bool"},
		{"key": "aggressive", "label": "Aggressive", "type": "bool"},
		{"key": "aggro_range", "label": "Aggro Range", "type": "int", "min": 1},
		{"key": "ai_package_state", "label": "AI Package", "type": "string"},
		{"key": "ai_action_state", "label": "AI Action", "type": "string"},
		{"key": "loot_table_name", "label": "Loot Table", "type": "string"},
		{"key": "spawn_timer", "label": "Spawn Timer (s)", "type": "int", "min": 0},
		{"key": "spawn_flags", "label": "Spawn Flags", "type": "text"},
		{"type": "separator"},
		{"key": "final_level", "label": "Final Level", "type": "int", "min": 1},
		{"key": "hitpoints", "label": "Hitpoints", "type": "int", "min": 0},
		{"key": "max_hitpoints", "label": "Max Hitpoints", "type": "int", "min": 1},
		{"key": "attack_speed", "label": "Attack Speed (ticks)", "type": "int", "min": 1},
		{"type": "separator"},
		{"key": "accuracy", "label": "Accuracy", "type": "int", "min": 1},
		{"key": "max_accuracy", "label": "Max Accuracy", "type": "int", "min": 1},
		{"key": "strength", "label": "Strength", "type": "int", "min": 1},
		{"key": "max_strength", "label": "Max Strength", "type": "int", "min": 1},
		{"key": "defence", "label": "Defence", "type": "int", "min": 1},
		{"key": "max_defence", "label": "Max Defence", "type": "int", "min": 1},
		{"key": "ether", "label": "Ether", "type": "int", "min": 1},
		{"key": "max_ether", "label": "Max Ether", "type": "int", "min": 1},
		{"type": "separator"},
		{"key": "melee_bonus", "label": "Melee Bonus", "type": "int", "min": 0},
		{"key": "ranged_bonus", "label": "Ranged Bonus", "type": "int", "min": 0},
		{"key": "ether_bonus", "label": "Ether Bonus", "type": "int", "min": 0},
		{"key": "melee_defense", "label": "Melee Defense", "type": "int", "min": 0},
		{"key": "ranged_defense", "label": "Ranged Defense", "type": "int", "min": 0},
		{"key": "ether_defense", "label": "Ether Defense", "type": "int", "min": 0},
	]
