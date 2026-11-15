extends DevPanel
## NPC editor — client-side port of the server NpcEditor.
##
## Layout:
##   Filter row (mode, search, npc dropdown)
##   Split:
##     Left: [Hitbox] [Scale] view toggles, then the 3D model preview
##     Right: view-specific content + Save All button at bottom
##
## View modes:
##   main    — full field inspector
##   hitbox  — hitbox config fields + 3D drag gizmo
##   scale   — mesh scale fields
##
## Hitbox and scale are keyed by render_slug, not NPC id. Multiple NPCs
## sharing a render_slug share the same hitbox and scale. Templates share
## the same slug as their uniques.
##
## Save semantics:
##   - Local [Save] in hitbox/scale views saves just that piece
##   - Save All saves everything dirty (fields + hitbox + scale)
##   - Fields go via npc_editor_save or dev_npc_editor_save_template
##   - Hitbox/scale go via npc_render_save_hitbox / npc_render_save_render
##
## FEATURES/COMBAT/EXTRA section headers organize the field list per the
## design doc (npc-editor-ascii.txt).
##
## Deferred: mesh-scale view will eventually get its own drag gizmo (per the
## design note). For now the fields are the only way to edit scale.
##
## Deferred: editing tile_x/y/z updates the DB immediately but the live NPC
## keeps its current position until respawn. Kill via [Admin] Kill if you
## need the new position now.

const TARGET_HEIGHT_BY_SLUG := {
	"player": 1.8, "man": 1.8, "guard": 1.8,
	"goblin": 1.4, "chicken": 0.4,
}

const AI_PACKAGES := ["idle", "wander"]

@export var model_viewport_min_size: Vector2 = Vector2(280, 360)
@export var default_orbit_yaw: float = 0.6
@export var default_orbit_pitch: float = 0.35
@export var default_orbit_distance: float = 4.0
@export var orbit_mouse_sensitivity: float = 0.008
@export var orbit_zoom_step: float = 0.9
@export var orbit_min_distance: float = 0.5
@export var orbit_max_distance: float = 50.0
@export var orbit_pitch_clamp: float = 1.4
@export var left_column_ratio: float = 0.25
@export var field_label_width: int = 90
@export var field_row_separation: int = 6
@export var field_widget_min_height: int = 60
@export var save_all_button_height: int = 32
@export var hitbox_capsule_alpha: float = 0.375
# -- Grid constants --
# Every cell in every row is exactly GRID_CELL_WIDTH px wide: label (fixed) +
# separation + spin (fixed). Because every row contains exactly GRID_COLS
# items at this width, columns align vertically by construction — no
# dependence on GridContainer's "size columns to widest cell" heuristics.
const GRID_COLS := 4
const GRID_CELL_WIDTH := 151
const GRID_LABEL_WIDTH := 85
const GRID_SPIN_WIDTH := 62
const GRID_LABEL_SPIN_SEP := 4
const GRID_CELL_SEP := 12

# -- Sizing --
const LINE_EDIT_WIDTH := 130
const DROPDOWN_WIDTH := 140
const TEXTAREA_MIN_WIDTH := 200
const TEXTAREA_MIN_HEIGHT := 100

var _mode: String = "unique"
var _view_mode: String = "main"   # "main", "hitbox", "scale"

# Widgets
var _mode_selector: OptionButton
var _search_edit: LineEdit
var _npc_selector: OptionButton
var _preview_placeholder: Label
var _fields_container: VBoxContainer
var _save_all_button: Button
var _hitbox_toggle: Button
var _scale_toggle: Button

# Hitbox UI (rebuilt per view; kept as members so save handlers can reach)
var _hitbox_type_selector: OptionButton
var _hitbox_radius_spin: SpinBox
var _hitbox_height_spin: SpinBox
var _hitbox_offset_x_spin: SpinBox
var _hitbox_offset_y_spin: SpinBox
var _hitbox_offset_z_spin: SpinBox
var _hitbox_save_button: Button
var _hitbox_reset_button: Button

# Scale UI
var _scale_x_spin: SpinBox
var _scale_y_spin: SpinBox
var _scale_z_spin: SpinBox
var _scale_save_button: Button
var _scale_reset_button: Button

# 3D
var _sub_viewport: SubViewport
var _sub_viewport_container: SubViewportContainer
var _camera: Camera3D
var _model_holder: Node3D
var _gizmo: HitboxGizmo = null
var _current_model: Node3D = null
var _current_slug: String = ""
var _current_data: Dictionary = {}

# Model state
var _base_fit_factor: float = 1.0
var _user_scale: Vector3 = Vector3.ONE
var _hitbox: Dictionary = {}
var _hitbox_dirty: bool = false
var _scale_dirty: bool = false

# Options cache (render slugs, dialogue scripts, loot lists, credits loot lists)
var _options: Dictionary = {}
var _options_loaded: bool = false
var _options_pending: bool = false

# List state
var _items: Array = []
var _filtered_items: Array = []
var _current_id: int = 0
var _list_loaded: bool = false
var _pending_npc_id: int = 0

# Field widgets
var _fields: Dictionary = {}

# Orbit state
var _orbit_yaw: float = 0.6
var _orbit_pitch: float = 0.35
var _orbit_distance: float = 4.0
var _orbit_target: Vector3 = Vector3(0, 1, 0)
var _orbiting: bool = false

var _split: HSplitContainer
var _style: DevStyle = null

func _ready() -> void:
	_style = DevStyle.instance
	_build_ui()
	_connect_network()
	_orbit_yaw = default_orbit_yaw
	_orbit_pitch = default_orbit_pitch
	_orbit_distance = default_orbit_distance
	_request_options()

# ---------- DevPanel hooks ----------

func panel_opened() -> void:
	if not _list_loaded:
		_reload_list()
	if not _options_loaded and not _options_pending:
		_request_options()

func on_hub_open_for_entity(entity_type: String, entity_id: int) -> void:
	if entity_type != "npc": return
	if _mode != "unique":
		_set_mode("unique")
	_pending_npc_id = entity_id
	if _list_loaded:
		_try_select_pending()

# ---------- Options ----------

func _request_options() -> void:
	_options_pending = true
	Network.send_dev_npc_editor_options_request()

func _on_options_result(ok: bool, options: Dictionary) -> void:
	_options_pending = false
	if not ok: return
	_options = options
	_options_loaded = true
	if not _current_data.is_empty():
		_rebuild_right_panel()

# ---------- UI construction ----------

func _build_ui() -> void:
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", _style.panel_separation)
	add_child(vbox)

	# Filter row
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

	# Split
	var split := HSplitContainer.new()
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vbox.add_child(split)

	# Left column: view toggles + model preview
	var left := VBoxContainer.new()
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	left.size_flags_vertical = Control.SIZE_EXPAND_FILL
	left.add_theme_constant_override("separation", 4)
	split.add_child(left)

	var toggle_row := HBoxContainer.new()
	toggle_row.add_theme_constant_override("separation", 4)
	left.add_child(toggle_row)

	_hitbox_toggle = _make_view_toggle(Texts.get_text("ui.dev_hub.npc_editor.view_hitbox"), "hitbox")
	toggle_row.add_child(_hitbox_toggle)

	_scale_toggle = _make_view_toggle(Texts.get_text("ui.dev_hub.npc_editor.view_scale"), "scale")
	toggle_row.add_child(_scale_toggle)

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

	_gizmo = HitboxGizmo.new()
	_gizmo.camera = _camera
	_gizmo.hitbox_capsule_alpha = hitbox_capsule_alpha
	_sub_viewport.add_child(_gizmo)
	_gizmo.hitbox_changed.connect(_on_gizmo_hitbox_changed)
	_gizmo.set_gizmo_visible(false)

	_preview_placeholder = Label.new()
	_preview_placeholder.text = Texts.get_text("ui.dev_hub.npc_editor.preview_placeholder")
	_preview_placeholder.add_theme_font_size_override("font_size", _style.font_size_body)
	_preview_placeholder.add_theme_color_override("font_color", _style.text_muted)
	_preview_placeholder.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_preview_placeholder.set_anchors_and_offsets_preset(Control.PRESET_CENTER_TOP)
	_sub_viewport_container.add_child(_preview_placeholder)

	# Right column: inset from the hub's right edge by the SAME amount the
	# model viewer is inset from the left. The hub's content_margin already
	# provides 16px on every side; we don't add extra right padding here or
	# the columns will be visually asymmetric. clip_contents prevents
	# wide children from pushing the split past the panel boundary.
	var right_margin := MarginContainer.new()
	right_margin.add_theme_constant_override("margin_left", _style.panel_separation)
	right_margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right_margin.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right_margin.clip_contents = true
	split.add_child(right_margin)

	var right := VBoxContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right.add_theme_constant_override("separation", _style.panel_separation)
	right.clip_contents = true
	right_margin.add_child(right)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	right.add_child(scroll)

	_fields_container = VBoxContainer.new()
	_fields_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_fields_container.add_theme_constant_override("separation", 4)
	scroll.add_child(_fields_container)

	_save_all_button = Button.new()
	_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")
	_save_all_button.disabled = true
	_save_all_button.focus_mode = Control.FOCUS_NONE
	_save_all_button.custom_minimum_size = Vector2(0, save_all_button_height)
	_save_all_button.add_theme_font_size_override("font_size", _style.font_size_body)
	_save_all_button.add_theme_color_override("font_color", _style.text_primary)
	_save_all_button.add_theme_color_override("font_hover_color", _style.text_primary)
	_save_all_button.add_theme_color_override("font_pressed_color", _style.text_primary)
	_save_all_button.add_theme_color_override("font_disabled_color", _style.text_muted)
	_save_all_button.pressed.connect(_on_save_all_pressed)
	right.add_child(_save_all_button)

	# Split sizing
	_split = split
	split.resized.connect(_recompute_split)
	call_deferred("_recompute_split")

	_update_camera_orbit()
	_sync_view_toggles()


func _recompute_split() -> void:
	if _split == null: return
	var w := _split.size.x
	if w <= 0: return
	var target := int(w * left_column_ratio) - w / 2
	if _split.split_offset != target:
		_split.split_offset = target


func _make_view_toggle(text: String, view_id: String) -> Button:
	var b := Button.new()
	b.text = text
	b.toggle_mode = true
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(0, 56)
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.alignment = HORIZONTAL_ALIGNMENT_CENTER
	b.add_theme_font_size_override("font_size", _style.font_size_small)
	b.add_theme_color_override("font_color", _style.text_primary)
	b.add_theme_color_override("font_hover_color", _style.text_primary)
	b.add_theme_color_override("font_pressed_color", _style.text_heading)
	b.add_theme_color_override("font_hover_pressed_color", _style.text_heading)
	b.pressed.connect(_on_view_toggle_pressed.bind(view_id))
	return b

# ---------- View mode ----------

func _on_view_toggle_pressed(view_id: String) -> void:
	if _view_mode == view_id:
		_view_mode = "main"
	else:
		_view_mode = view_id
	_sync_view_toggles()
	_rebuild_right_panel()
	_update_hitbox_display()

func _sync_view_toggles() -> void:
	if _hitbox_toggle: _hitbox_toggle.set_pressed_no_signal(_view_mode == "hitbox")
	if _scale_toggle:  _scale_toggle.set_pressed_no_signal(_view_mode == "scale")

# ---------- Network ----------

func _connect_network() -> void:
	Network.dev_npc_editor_options_result.connect(_on_options_result)
	Network.dev_npc_editor_list_result.connect(_on_list_result)
	Network.dev_npc_editor_template_data.connect(_on_template_data)
	Network.dev_npc_editor_template_saved.connect(_on_template_saved)
	Network.npc_editor_data.connect(_on_npc_editor_data)
	Network.npc_editor_saved.connect(_on_npc_editor_saved)
	Network.npc_render_save_result.connect(_on_npc_render_save_result)

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
	_rebuild_right_panel()

func _on_template_data(ok: bool, template: Dictionary) -> void:
	if _mode != "template": return
	if not ok: return
	if int(template.get("id", 0)) != _current_id: return
	_current_data = template
	var sid := str(template.get("slug_id", ""))
	_load_model(sid.trim_prefix("npc_"))
	_rebuild_right_panel()

# ---------- Model ----------

func _load_model(slug: String) -> void:
	if slug == "":
		_clear_model_only()
		return
	var is_same_model := slug == _current_slug
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

	_user_scale = _load_scale_for_slug(slug)
	_compute_base_fit(slug)
	_apply_model_scale()

	if not is_same_model:
		_frame_camera(inst)
	_update_camera_orbit()

	_hitbox = _load_hitbox_for_slug(slug)
	_hitbox_dirty = false
	_scale_dirty = false
	_update_hitbox_display()

func _compute_base_fit(slug: String) -> void:
	_base_fit_factor = 1.0
	if _current_model == null: return
	var aabb := _measure_world_aabb(_current_model)
	if aabb.size.y < 0.001: return
	var target_h: float = TARGET_HEIGHT_BY_SLUG.get(slug, 1.8)
	_base_fit_factor = target_h / aabb.size.y

func _apply_model_scale() -> void:
	if _current_model == null: return
	_current_model.scale = Vector3.ONE * _base_fit_factor * _user_scale
	_current_model.position = Vector3.ZERO
	var aabb := _measure_world_aabb(_current_model)
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
	if _gizmo:
		_gizmo.set_gizmo_visible(false)

func _clear_model() -> void:
	_clear_model_only()
	_current_data = {}
	_current_slug = ""
	_hitbox = {}
	_hitbox_dirty = false
	_scale_dirty = false
	if _fields_container:
		for c in _fields_container.get_children():
			_fields_container.remove_child(c)
			c.queue_free()
	_fields.clear()
	if _save_all_button:
		_save_all_button.disabled = true
		_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")

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

# ---------- Viewport input ----------

func _on_viewport_input(event: InputEvent) -> void:
	# Gizmo gets first crack at left-click/motion when in hitbox view
	if _gizmo != null and _view_mode == "hitbox":
		if event is InputEventMouseButton or event is InputEventMouseMotion:
			if _gizmo.handle_input(event, event.position):
				get_viewport().set_input_as_handled()
				return

	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_orbiting = mb.pressed
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_orbit_distance = maxf(orbit_min_distance, _orbit_distance * orbit_zoom_step)
			_update_camera_orbit()
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_orbit_distance = minf(orbit_max_distance, _orbit_distance / orbit_zoom_step)
			_update_camera_orbit()
			get_viewport().set_input_as_handled()
			return
	elif event is InputEventMouseMotion and _orbiting:
		var mm := event as InputEventMouseMotion
		_orbit_yaw -= mm.relative.x * orbit_mouse_sensitivity
		_orbit_pitch = clampf(
			_orbit_pitch + mm.relative.y * orbit_mouse_sensitivity,
			-orbit_pitch_clamp, orbit_pitch_clamp)
		_update_camera_orbit()

# ---------- Right panel rebuild ----------

func _rebuild_right_panel() -> void:
	for c in _fields_container.get_children():
		_fields_container.remove_child(c)
		c.queue_free()
	_fields.clear()

	if _current_data.is_empty():
		_save_all_button.disabled = true
		return

	match _view_mode:
		"hitbox":
			_build_hitbox_view()
		"scale":
			_build_scale_view()
		_:
			_build_main_view()

	_save_all_button.disabled = false
	_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")


# ---------- Main view ----------
func _build_main_view() -> void:
	if _mode == "template":
		_build_template_view()
	else:
		_build_unique_view()


func _build_unique_view() -> void:
	# Row 1: Name (readonly) | Display
	var r1 := _field_row()
	_add_line_edit_to_row(r1, "label_name", "name", str(_current_data.get("name", "")), true)
	_add_line_edit_to_row(r1, "label_display", "display", str(_current_data.get("display", "")), false)
	_fields_container.add_child(r1)

	# Row 2: Render | Dialogue Script
	var r2 := _field_row()
	_add_dropdown_to_row(r2, "label_render", "render_slug",
		str(_current_data.get("render_slug", "")),
		_options.get("render_slugs", []))
	_add_dropdown_to_row(r2, "label_dialogue_script", "dialogue_script",
		str(_current_data.get("dialogue_script", "")),
		["_none"] + _options.get("dialogue_scripts", []),
		{"_none": ""})
	_fields_container.add_child(r2)

	_fields_container.add_child(HSeparator.new())

	# Row 3: Loot Table | Credits Table
	var r3 := _field_row()
	_add_dropdown_to_row(r3, "label_loot_table", "loot_table_name",
		str(_current_data.get("loot_table_name", "")),
		_options.get("loot_lists", []))
	_add_dropdown_to_row(r3, "label_credits_table", "credits_table_name",
		str(_current_data.get("credits_table_name", "")),
		_options.get("credits_loot_lists", []))
	_fields_container.add_child(r3)

	# Row 4: AI Package
	var r4 := _field_row()
	_add_dropdown_to_row(r4, "label_ai_package", "ai_package_state",
		str(_current_data.get("ai_package_state", "idle")),
		AI_PACKAGES)
	_fields_container.add_child(r4)

	# Row 6: Spawn Flags
	var r6 := _field_row()
	_add_textarea_to_row(r6, "label_spawn_flags", "spawn_flags",
		str(_current_data.get("spawn_flags", "{}")))
	_fields_container.add_child(r6)

	# Row 7: Tile X | Y | Z
	_fields_container.add_child(HSeparator.new())
	_fields_container.add_child(_section_header("section_tile_spawn"))

	var r_tile := _field_row()
	_add_spin_to_row(r_tile, "label_tile_x", "tile_x", int(_current_data.get("tile_x", 0)), -999999, 999999)
	_add_spin_to_row(r_tile, "label_tile_y", "tile_y", int(_current_data.get("tile_y", 0)), -999999, 999999)
	_add_spin_to_row(r_tile, "label_tile_z", "tile_z", int(_current_data.get("tile_z", 100)), -999999, 999999)
	_fields_container.add_child(r_tile)

	_fields_container.add_child(HSeparator.new())

	# FEATURES section
	_fields_container.add_child(_section_header("section_features"))

	var r_features := _field_row()
	_add_checkbox_to_row(r_features, "label_is_banker", "is_banker", bool(_current_data.get("is_banker", false)))
	_add_checkbox_to_row(r_features, "label_attackable", "attackable", bool(_current_data.get("attackable", true)))
	_add_checkbox_to_row(r_features, "label_aggressive", "aggressive", bool(_current_data.get("aggressive", false)))
	_fields_container.add_child(r_features)

	_fields_container.add_child(HSeparator.new())

	# COMBAT STATS section
	_fields_container.add_child(_section_header("section_combat"))
	_build_combat_stats_grid()


func _build_template_view() -> void:
	var r1 := _field_row()
	_add_readonly_to_row(r1, "slug_id", str(_current_data.get("slug_id", "")))
	_fields_container.add_child(r1)

	_fields_container.add_child(HSeparator.new())
	_fields_container.add_child(_section_header("section_combat"))
	_build_combat_stats_grid()

# ---------- Hitbox view ----------

func _build_hitbox_view() -> void:
	var is_capsule := str(_hitbox.get("type", "capsule")) == "capsule"

	# Type
	var r_type := _field_row()
	var type_lbl := _inline_label(Texts.get_text("ui.dev_hub.npc_editor.view_hitbox_type"))
	_hitbox_type_selector = OptionButton.new()
	_hitbox_type_selector.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_hitbox_type_selector.add_item(Texts.get_text("ui.dev_hub.npc_editor.hitbox_type_sphere"), 0)
	_hitbox_type_selector.add_item(Texts.get_text("ui.dev_hub.npc_editor.hitbox_type_capsule"), 1)
	_hitbox_type_selector.select(1 if is_capsule else 0)
	_hitbox_type_selector.item_selected.connect(_on_hitbox_type_selected)
	r_type.add_child(type_lbl)
	r_type.add_child(_hitbox_type_selector)
	_fields_container.add_child(r_type)

	# Radius
	var r_rad := _field_row()
	_hitbox_radius_spin = _inline_spin(0.05, 10.0, 0.05, float(_hitbox.get("radius", 0.4)))
	_hitbox_radius_spin.value_changed.connect(_on_hitbox_radius_changed)
	_add_widget_with_label(r_rad, Texts.get_text("ui.dev_hub.npc_editor.hitbox_radius"), _hitbox_radius_spin)
	_fields_container.add_child(r_rad)

	# Height (only for capsule)
	if is_capsule:
		var r_h := _field_row()
		_hitbox_height_spin = _inline_spin(0.1, 10.0, 0.05, float(_hitbox.get("height", 1.8)))
		_hitbox_height_spin.value_changed.connect(_on_hitbox_height_changed)
		_add_widget_with_label(r_h, Texts.get_text("ui.dev_hub.npc_editor.hitbox_height"), _hitbox_height_spin)
		_fields_container.add_child(r_h)
	else:
		_hitbox_height_spin = null

	# X Y Z offset
	var r_off := _field_row()
	_hitbox_offset_x_spin = _inline_spin(-10.0, 10.0, 0.05, float(_hitbox.get("offset_x", 0.0)))
	_hitbox_offset_x_spin.value_changed.connect(_on_hitbox_offset_changed)
	_add_widget_with_label(r_off, Texts.get_text("ui.dev_hub.npc_editor.hitbox_offset_x"), _hitbox_offset_x_spin)

	_hitbox_offset_y_spin = _inline_spin(-10.0, 10.0, 0.05, float(_hitbox.get("offset_y", 0.9)))
	_hitbox_offset_y_spin.value_changed.connect(_on_hitbox_offset_changed)
	_add_widget_with_label(r_off, Texts.get_text("ui.dev_hub.npc_editor.hitbox_offset_y"), _hitbox_offset_y_spin)

	_hitbox_offset_z_spin = _inline_spin(-10.0, 10.0, 0.05, float(_hitbox.get("offset_z", 0.0)))
	_hitbox_offset_z_spin.value_changed.connect(_on_hitbox_offset_changed)
	_add_widget_with_label(r_off, Texts.get_text("ui.dev_hub.npc_editor.hitbox_offset_z"), _hitbox_offset_z_spin)
	_fields_container.add_child(r_off)

	# Save / Reset
	var r_btns := _field_row()
	_hitbox_save_button = _small_button(_hitbox_save_label())
	_hitbox_save_button.pressed.connect(_on_hitbox_save_pressed)
	r_btns.add_child(_hitbox_save_button)

	_hitbox_reset_button = _small_button(Texts.get_text("ui.dev_hub.npc_editor.hitbox_reset"))
	_hitbox_reset_button.pressed.connect(_on_hitbox_reset_pressed)
	r_btns.add_child(_hitbox_reset_button)
	_fields_container.add_child(r_btns)

	_push_hitbox_to_gizmo()

# ---------- Scale view ----------

func _build_scale_view() -> void:
	var r1 := _field_row()
	_scale_x_spin = _inline_spin(0.1, 5.0, 0.05, _user_scale.x)
	_scale_x_spin.value_changed.connect(_on_scale_changed)
	_add_widget_with_label(r1, Texts.get_text("ui.dev_hub.npc_editor.scale_x"), _scale_x_spin)

	_scale_y_spin = _inline_spin(0.1, 5.0, 0.05, _user_scale.y)
	_scale_y_spin.value_changed.connect(_on_scale_changed)
	_add_widget_with_label(r1, Texts.get_text("ui.dev_hub.npc_editor.scale_y"), _scale_y_spin)

	_scale_z_spin = _inline_spin(0.1, 5.0, 0.05, _user_scale.z)
	_scale_z_spin.value_changed.connect(_on_scale_changed)
	_add_widget_with_label(r1, Texts.get_text("ui.dev_hub.npc_editor.scale_z"), _scale_z_spin)
	_fields_container.add_child(r1)

	var r2 := _field_row()
	_scale_save_button = _small_button(_scale_save_label())
	_scale_save_button.pressed.connect(_on_scale_save_pressed)
	r2.add_child(_scale_save_button)

	_scale_reset_button = _small_button(Texts.get_text("ui.dev_hub.npc_editor.scale_reset"))
	_scale_reset_button.pressed.connect(_on_scale_reset_pressed)
	r2.add_child(_scale_reset_button)
	_fields_container.add_child(r2)


# ---------- Widget builders ----------
func _field_row() -> HBoxContainer:
	var r := HBoxContainer.new()
	r.add_theme_constant_override("separation", field_row_separation)
	return r


func _inline_label(text: String) -> Label:
	var l := Label.new()
	l.text = text
	l.custom_minimum_size = Vector2(field_label_width, 0)
	l.clip_text = true
	l.add_theme_font_size_override("font_size", _style.font_size_small)
	l.add_theme_color_override("font_color", _style.text_muted)
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	return l


func _add_spin_to_row(row: HBoxContainer, label_key: String, save_key: String, value: int, minv: int, maxv: int) -> void:
	# Compact label — sized to its own text, no fixed min-width. Right for
	# short labels like "X", "Y", "Z" and "Spawn Timer" where the label
	# should sit directly against its spinner.
	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.npc_editor." + label_key)
	lbl.add_theme_font_size_override("font_size", _style.font_size_small)
	lbl.add_theme_color_override("font_color", _style.text_muted)
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	row.add_child(lbl)
	var sb := _inline_spin(minv, maxv, 1, value)
	row.add_child(sb)
	_fields[save_key] = sb


func _inline_spin(minv: float, maxv: float, step: float, val: float) -> SpinBox:
	var s := SpinBox.new()
	s.min_value = minv
	s.max_value = maxv
	s.step = step
	s.value = val
	s.custom_minimum_size = Vector2(72, 0)
	s.add_theme_font_size_override("font_size", _style.font_size_small)
	return s


func _small_button(text: String) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.add_theme_font_size_override("font_size", _style.font_size_small)
	b.add_theme_color_override("font_color", _style.text_primary)
	b.add_theme_color_override("font_hover_color", _style.text_primary)
	b.add_theme_color_override("font_pressed_color", _style.text_primary)
	return b


func _build_combat_stats_grid() -> void:
	var grid := VBoxContainer.new()
	grid.add_theme_constant_override("separation", 4)
	_fields_container.add_child(grid)

	# Row 1: Attack Speed, Aggro Range, Spawn Timer
	_add_grid_row(grid, [
		_grid_spec("label_attack_speed", "attack_speed", int(_current_data.get("attack_speed", 10)), 1, 999),
		_grid_spec("label_aggro_range", "aggro_range", int(_current_data.get("aggro_range", 8)), 1, 999),
		_grid_spec("label_spawn_timer", "spawn_timer", int(_current_data.get("spawn_timer", 3)), 0, 999999),
	])

	# Row 2: Final Level
	_add_grid_row(grid, [
		_grid_spec("label_final_level", "final_level", int(_current_data.get("final_level", 1)), 1, 999),
	])

	# Row 3: Hitpoints
	_add_grid_row(grid, [
		_grid_spec("label_hitpoints", "max_hitpoints", int(_current_data.get("max_hitpoints", 10)), 1, 999999),
	])

	# Row 4: Accuracy, Strength, Defence, Ether
	_add_grid_row(grid, [
		_grid_spec("label_accuracy", "max_accuracy", int(_current_data.get("max_accuracy", 1)), 1, 999999),
		_grid_spec("label_strength", "max_strength", int(_current_data.get("max_strength", 1)), 1, 999999),
		_grid_spec("label_defence", "max_defence", int(_current_data.get("max_defence", 1)), 1, 999999),
		_grid_spec("label_ether", "max_ether", int(_current_data.get("max_ether", 1)), 1, 999999),
	])

	# Row 5: Melee Bonus, Ranged Bonus, Ether Bonus
	_add_grid_row(grid, [
		_grid_spec("label_melee_bonus", "melee_bonus", int(_current_data.get("melee_bonus", 0)), 0, 999999),
		_grid_spec("label_ranged_bonus", "ranged_bonus", int(_current_data.get("ranged_bonus", 0)), 0, 999999),
		_grid_spec("label_ether_bonus", "ether_bonus", int(_current_data.get("ether_bonus", 0)), 0, 999999),
	])

	# Row 6: Melee Defense, Ranged Defense, Ether Defense
	_add_grid_row(grid, [
		_grid_spec("label_melee_defense", "melee_defense", int(_current_data.get("melee_defense", 0)), 0, 999999),
		_grid_spec("label_ranged_defense", "ranged_defense", int(_current_data.get("ranged_defense", 0)), 0, 999999),
		_grid_spec("label_ether_defense", "ether_defense", int(_current_data.get("ether_defense", 0)), 0, 999999),
	])


func _grid_spec(label_key: String, save_key: String, value: int, minv: int, maxv: int) -> Dictionary:
	return {"label": label_key, "key": save_key, "val": value, "min": minv, "max": maxv}


func _add_grid_row(parent: VBoxContainer, cells: Array) -> void:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", GRID_CELL_SEP)
	parent.add_child(row)
	for i in range(GRID_COLS):
		if i < cells.size():
			_add_grid_cell(row, cells[i])
		else:
			_add_grid_spacer(row)


func _add_grid_cell(row: HBoxContainer, spec: Dictionary) -> void:
	# Fixed-width cell: fixed-width label + fixed-width spin + fixed
	# separation. Every cell in every row uses these same constants, so
	# columns align vertically regardless of label text length. The label
	# uses clip_text so long text (e.g. "Ranged Defense") can't force the
	# cell wider than GRID_CELL_WIDTH.
	var cell := HBoxContainer.new()
	cell.custom_minimum_size = Vector2(GRID_CELL_WIDTH, 0)
	cell.add_theme_constant_override("separation", GRID_LABEL_SPIN_SEP)

	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.npc_editor." + spec["label"])
	lbl.custom_minimum_size = Vector2(GRID_LABEL_WIDTH, 0)
	lbl.clip_text = true
	lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	lbl.add_theme_font_size_override("font_size", _style.font_size_small)
	lbl.add_theme_color_override("font_color", _style.text_muted)
	cell.add_child(lbl)

	var sb := SpinBox.new()
	sb.min_value = spec["min"]
	sb.max_value = spec["max"]
	sb.step = 1
	sb.value = spec["val"]
	sb.custom_minimum_size = Vector2(GRID_SPIN_WIDTH, 0)
	sb.add_theme_font_size_override("font_size", _style.font_size_small)
	cell.add_child(sb)

	row.add_child(cell)
	_fields[spec["key"]] = sb


func _add_grid_spacer(row: HBoxContainer) -> void:
	var spacer := Control.new()
	spacer.custom_minimum_size = Vector2(GRID_CELL_WIDTH, 0)
	row.add_child(spacer)


func _apply_toggle_style(btn: Button, on: bool, label_key: String) -> void:
	var base_text := Texts.get_text("ui.dev_hub.npc_editor." + label_key)
	btn.text = ("[x] " if on else "[ ] ") + base_text
	var sb := StyleBoxFlat.new()
	if on:
		sb.bg_color = Color(0.18, 0.32, 0.18, 1.0)
		sb.border_color = Color(0.45, 0.85, 0.45, 1.0)
	else:
		sb.bg_color = Color(0.20, 0.20, 0.24, 1.0)
		sb.border_color = Color(0.38, 0.38, 0.44, 1.0)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = 3
	sb.corner_radius_top_right = 3
	sb.corner_radius_bottom_left = 3
	sb.corner_radius_bottom_right = 3
	sb.content_margin_left = 10
	sb.content_margin_right = 10
	sb.content_margin_top = 5
	sb.content_margin_bottom = 5
	btn.add_theme_stylebox_override("normal", sb)
	btn.add_theme_stylebox_override("hover", sb)
	btn.add_theme_stylebox_override("pressed", sb)
	btn.add_theme_stylebox_override("hover_pressed", sb)
	btn.add_theme_stylebox_override("disabled", sb)


func _section_header(key: String) -> Control:
	var box := CenterContainer.new()
	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.npc_editor." + key)
	lbl.add_theme_font_size_override("font_size", _style.font_size_tab)
	lbl.add_theme_color_override("font_color", _style.text_heading)
	box.add_child(lbl)
	return box


func _add_widget_with_label(row: HBoxContainer, label_text: String, widget: Control) -> void:
	row.add_child(_inline_label(label_text))
	row.add_child(widget)


func _add_line_edit_to_row(row: HBoxContainer, label_key: String, save_key: String, value: String, readonly: bool) -> void:
	var le := LineEdit.new()
	le.text = value
	le.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, 0)
	le.add_theme_font_size_override("font_size", _style.font_size_small)
	if readonly:
		le.editable = false
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.npc_editor." + label_key)))
	row.add_child(le)
	_fields[save_key] = le


func _add_readonly_to_row(row: HBoxContainer, label_text: String, value: String) -> void:
	var lbl := Label.new()
	lbl.text = value
	lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	lbl.add_theme_font_size_override("font_size", _style.font_size_small)
	lbl.add_theme_color_override("font_color", _style.text_muted)
	row.add_child(_inline_label(label_text))
	row.add_child(lbl)


func _add_checkbox_to_row(row: HBoxContainer, label_key: String, save_key: String, pressed: bool) -> void:
	var btn := Button.new()
	btn.toggle_mode = true
	btn.button_pressed = pressed
	btn.focus_mode = Control.FOCUS_NONE
	btn.add_theme_font_size_override("font_size", _style.font_size_body)
	btn.add_theme_color_override("font_color", _style.text_primary)
	btn.add_theme_color_override("font_hover_color", _style.text_primary)
	btn.add_theme_color_override("font_pressed_color", Color(0.75, 1.0, 0.75))
	btn.add_theme_color_override("font_hover_pressed_color", Color(0.9, 1.0, 0.9))
	btn.add_theme_color_override("font_disabled_color", _style.text_muted)
	_apply_toggle_style(btn, pressed, label_key)
	btn.toggled.connect(func(p: bool) -> void: _apply_toggle_style(btn, p, label_key))
	row.add_child(btn)
	_fields[save_key] = btn


func _add_textarea_to_row(row: HBoxContainer, label_key: String, save_key: String, value: String) -> void:
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.npc_editor." + label_key)))
	var te := TextEdit.new()
	te.text = value
	te.custom_minimum_size = Vector2(TEXTAREA_MIN_WIDTH, TEXTAREA_MIN_HEIGHT)
	te.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	te.add_theme_font_size_override("font_size", _style.font_size_small)
	row.add_child(te)
	_fields[save_key] = te


func _add_dropdown_to_row(row: HBoxContainer, label_key: String, save_key: String, current: String, options: Array, label_to_value: Dictionary = {}) -> void:
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.npc_editor." + label_key)))
	var dd := OptionButton.new()
	dd.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	dd.add_theme_font_size_override("font_size", _style.font_size_small)
	dd.fit_to_longest_item = false
	dd.custom_minimum_size = Vector2(DROPDOWN_WIDTH, 0)
	var found := false
	for opt in options:
		var opt_str := str(opt)
		var idx := dd.item_count
		dd.add_item(opt_str)
		var value: String = str(label_to_value.get(opt_str, opt_str))
		dd.set_item_metadata(idx, value)
		if value == current or opt_str == current:
			dd.select(idx)
			found = true
	if not found:
		if current == "":
			# Preserve an empty value: no selection means save writes "".
			# Needed for optional fields like Credits Table where the NPC
			# may legitimately have no value.
			dd.select(-1)
		elif dd.item_count > 0:
			dd.select(0)
	row.add_child(dd)
	_fields[save_key] = dd


# ---------- Hitbox ----------
func _load_hitbox_for_slug(slug: String) -> Dictionary:
	var hb := NpcRender.get_hitbox(slug)
	if not hb.is_empty():
		return {
			"type": str(hb.get("type", "capsule")),
			"radius": float(hb.get("radius", 0.4)),
			"height": float(hb.get("height", 1.8)),
			"offset_x": float(hb.get("offset_x", 0.0)),
			"offset_y": float(hb.get("offset_y", 0.9)),
			"offset_z": float(hb.get("offset_z", 0.0)),
		}
	return _default_hitbox_for_model()

func _default_hitbox_for_model() -> Dictionary:
	if _current_model == null:
		return {
			"type": "capsule", "radius": 0.4, "height": 1.8,
			"offset_x": 0.0, "offset_y": 0.9, "offset_z": 0.0,
		}
	var aabb := _measure_world_aabb(_current_model)
	if aabb.size == Vector3.ZERO:
		return {
			"type": "capsule", "radius": 0.4, "height": 1.8,
			"offset_x": 0.0, "offset_y": 0.9, "offset_z": 0.0,
		}
	var center := aabb.get_center()
	var radius := maxf(maxf(aabb.size.x, aabb.size.z) * 0.5, 0.1)
	var height := maxf(aabb.size.y, radius * 2.0 + 0.1)
	return {
		"type": "capsule",
		"radius": radius,
		"height": height,
		"offset_x": center.x,
		"offset_y": center.y,
		"offset_z": center.z,
	}


func _update_hitbox_display() -> void:
	if _gizmo == null: return
	var should_show := _view_mode == "hitbox" and _current_slug != ""
	_gizmo.set_gizmo_visible(should_show)
	if should_show:
		_gizmo.set_hitbox(_hitbox)


func _push_hitbox_to_gizmo() -> void:
	if _gizmo != null:
		_gizmo.set_hitbox(_hitbox)


func _on_gizmo_hitbox_changed(new_config: Dictionary) -> void:
	_hitbox = new_config.duplicate()
	if _hitbox_radius_spin: _hitbox_radius_spin.set_value_no_signal(float(_hitbox.get("radius", 0.4)))
	if _hitbox_height_spin: _hitbox_height_spin.set_value_no_signal(float(_hitbox.get("height", 1.8)))
	if _hitbox_offset_x_spin: _hitbox_offset_x_spin.set_value_no_signal(float(_hitbox.get("offset_x", 0.0)))
	if _hitbox_offset_y_spin: _hitbox_offset_y_spin.set_value_no_signal(float(_hitbox.get("offset_y", 0.9)))
	if _hitbox_offset_z_spin: _hitbox_offset_z_spin.set_value_no_signal(float(_hitbox.get("offset_z", 0.0)))
	_mark_hitbox_dirty()


func _on_hitbox_type_selected(idx: int) -> void:
	_hitbox["type"] = "capsule" if idx == 1 else "sphere"
	_push_hitbox_to_gizmo()
	_mark_hitbox_dirty()
	_rebuild_right_panel()


func _on_hitbox_radius_changed(v: float) -> void:
	_hitbox["radius"] = v
	if str(_hitbox.get("type", "capsule")) == "capsule" and float(_hitbox.get("height", 1.8)) < v * 2.0 + 0.05:
		_hitbox["height"] = v * 2.0 + 0.05
		if _hitbox_height_spin: _hitbox_height_spin.set_value_no_signal(_hitbox["height"])
	_push_hitbox_to_gizmo()
	_mark_hitbox_dirty()


func _on_hitbox_height_changed(v: float) -> void:
	_hitbox["height"] = v
	_push_hitbox_to_gizmo()
	_mark_hitbox_dirty()


func _on_hitbox_offset_changed(_v: float) -> void:
	_hitbox["offset_x"] = _hitbox_offset_x_spin.value
	_hitbox["offset_y"] = _hitbox_offset_y_spin.value
	_hitbox["offset_z"] = _hitbox_offset_z_spin.value
	_push_hitbox_to_gizmo()
	_mark_hitbox_dirty()


func _mark_hitbox_dirty() -> void:
	_hitbox_dirty = true
	if _hitbox_save_button: _hitbox_save_button.text = _hitbox_save_label()


func _hitbox_save_label() -> String:
	var base := Texts.get_text("ui.dev_hub.npc_editor.hitbox_save")
	return (base + " *") if _hitbox_dirty else base


func _on_hitbox_save_pressed() -> void:
	if _current_slug == "":
		if _hitbox_save_button: _hitbox_save_button.text = "No Slug"
		await get_tree().create_timer(1.2).timeout
		if _hitbox_save_button: _hitbox_save_button.text = _hitbox_save_label()
		return
	if _hitbox_save_button: _hitbox_save_button.disabled = true
	_write_hitbox_to_cache(_current_slug, _hitbox)
	Network.send_npc_render_save_hitbox(_current_slug, _hitbox.duplicate())


func _on_hitbox_reset_pressed() -> void:
	_hitbox = _default_hitbox_for_model()
	_hitbox_dirty = true
	_push_hitbox_to_gizmo()
	_rebuild_right_panel()
	if _current_slug == "":
		return
	_write_hitbox_to_cache(_current_slug, _hitbox)
	Network.send_npc_render_save_hitbox(_current_slug, _hitbox.duplicate())


# ---------- Scale ----------
func _load_scale_for_slug(slug: String) -> Vector3:
	var rc := NpcRender.get_render_config(slug)
	if rc.is_empty(): return Vector3.ONE
	return Vector3(
		float(rc.get("scale_x", 1.0)),
		float(rc.get("scale_y", 1.0)),
		float(rc.get("scale_z", 1.0)))


func _mark_scale_dirty() -> void:
	_scale_dirty = true
	if _scale_save_button: _scale_save_button.text = _scale_save_label()


func _scale_save_label() -> String:
	var base := Texts.get_text("ui.dev_hub.npc_editor.scale_save")
	return (base + " *") if _scale_dirty else base


func _on_scale_changed(_v: float) -> void:
	_user_scale = Vector3(_scale_x_spin.value, _scale_y_spin.value, _scale_z_spin.value)
	_apply_model_scale()
	_mark_scale_dirty()


func _on_scale_save_pressed() -> void:
	if _current_slug == "":
		if _scale_save_button: _scale_save_button.text = "No Slug"
		await get_tree().create_timer(1.2).timeout
		if _scale_save_button: _scale_save_button.text = _scale_save_label()
		return
	if _scale_save_button: _scale_save_button.disabled = true
	var render := {
		"scale_x": _user_scale.x,
		"scale_y": _user_scale.y,
		"scale_z": _user_scale.z,
	}
	_write_render_to_cache(_current_slug, render)
	Network.send_npc_render_save_render(_current_slug, render)

func _on_scale_reset_pressed() -> void:
	_user_scale = Vector3.ONE
	_scale_dirty = true
	_apply_model_scale()
	_rebuild_right_panel()
	if _current_slug == "":
		return
	var render := {"scale_x": 1.0, "scale_y": 1.0, "scale_z": 1.0}
	_write_render_to_cache(_current_slug, render)
	Network.send_npc_render_save_render(_current_slug, render)


# ---------- Save All ----------
func _on_save_all_pressed() -> void:
	if _current_id == 0: return

	# Flush dirty hitbox and scale first, writing each to cache at send time
	if _hitbox_dirty and _current_slug != "":
		_write_hitbox_to_cache(_current_slug, _hitbox)
		Network.send_npc_render_save_hitbox(_current_slug, _hitbox.duplicate())
		_hitbox_dirty = false
		if _hitbox_save_button: _hitbox_save_button.text = _hitbox_save_label()

	if _scale_dirty and _current_slug != "":
		var render := {
			"scale_x": _user_scale.x,
			"scale_y": _user_scale.y,
			"scale_z": _user_scale.z,
		}
		_write_render_to_cache(_current_slug, render)
		Network.send_npc_render_save_render(_current_slug, render)
		_scale_dirty = false
		if _scale_save_button: _scale_save_button.text = _scale_save_label()

	# Save fields
	_save_all_button.disabled = true
	_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_saving")

	var updates := {}
	for key in _fields.keys():
		var c = _fields[key]
		if c is SpinBox: updates[key] = int((c as SpinBox).value)
		elif c is LineEdit: updates[key] = (c as LineEdit).text
		elif c is TextEdit: updates[key] = (c as TextEdit).text
		elif c is OptionButton:
			# MUST come before Button — OptionButton extends Button
			var idx: int = (c as OptionButton).selected
			if idx >= 0:
				var md = (c as OptionButton).get_item_metadata(idx)
				updates[key] = str(md) if md != null else ""
			else:
				updates[key] = ""
		elif c is Button:
			# Checkboxes are Button-in-toggle-mode now
			updates[key] = (c as Button).button_pressed

	if _mode == "unique":
		Network.send_npc_editor_save(_current_id, updates)
	else:
		Network.send_dev_npc_editor_save_template(_current_id, updates)


# ---------- Save callbacks ----------
func _on_npc_editor_saved(ok: bool, npc_id: int) -> void:
	if _mode != "unique": return
	if npc_id != _current_id: return
	if not _save_all_button: return
	_save_all_button.disabled = false
	if ok:
		_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_saved")
		Network.send_npc_editor_get(_current_id)
		await get_tree().create_timer(1.2).timeout
		if _save_all_button and _save_all_button.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_saved"):
			_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")
	else:
		_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_failed")
		await get_tree().create_timer(2.0).timeout
		if _save_all_button and _save_all_button.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_failed"):
			_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")


func _on_template_saved(ok: bool, template_id: int) -> void:
	if _mode != "template": return
	if template_id != _current_id: return
	if not _save_all_button: return
	_save_all_button.disabled = false
	if ok:
		_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_saved")
		Network.send_dev_npc_editor_get_template(_current_id)
		await get_tree().create_timer(1.2).timeout
		if _save_all_button and _save_all_button.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_saved"):
			_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")
	else:
		_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_failed")
		await get_tree().create_timer(2.0).timeout
		if _save_all_button and _save_all_button.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_failed"):
			_save_all_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")

func _on_npc_render_save_result(ok: bool, kind: String, slug: String) -> void:
	if slug != _current_slug: return
	if kind == "hitbox":
		if _hitbox_save_button: _hitbox_save_button.disabled = false
		if ok:
			_hitbox_dirty = false
			if _hitbox_save_button:
				_hitbox_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.hitbox_saved")
				await get_tree().create_timer(1.2).timeout
				if _hitbox_save_button and _hitbox_save_button.text == Texts.get_text("ui.dev_hub.npc_editor.hitbox_saved"):
					_hitbox_save_button.text = _hitbox_save_label()
		else:
			if _hitbox_save_button:
				_hitbox_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.hitbox_save_failed")
				await get_tree().create_timer(1.5).timeout
				if _hitbox_save_button and _hitbox_save_button.text == Texts.get_text("ui.dev_hub.npc_editor.hitbox_save_failed"):
					_hitbox_save_button.text = _hitbox_save_label()
	elif kind == "render":
		if _scale_save_button: _scale_save_button.disabled = false
		if ok:
			_scale_dirty = false
			if _scale_save_button:
				_scale_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.scale_saved")
				await get_tree().create_timer(1.2).timeout
				if _scale_save_button and _scale_save_button.text == Texts.get_text("ui.dev_hub.npc_editor.scale_saved"):
					_scale_save_button.text = _scale_save_label()
		else:
			if _scale_save_button:
				_scale_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.scale_save_failed")
				await get_tree().create_timer(1.5).timeout
				if _scale_save_button and _scale_save_button.text == Texts.get_text("ui.dev_hub.npc_editor.scale_save_failed"):
					_scale_save_button.text = _scale_save_label()


# ---------- Cache writes ----------
func _write_hitbox_to_cache(slug: String, hitbox: Dictionary) -> void:
	if slug.is_empty(): return
	var dir := "%s/%s" % [NpcRender.CACHE_DIR, slug]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open("%s/hitbox.json" % dir, FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(hitbox, "\t"))
	f.close()


func _write_render_to_cache(slug: String, render: Dictionary) -> void:
	if slug.is_empty(): return
	var dir := "%s/%s" % [NpcRender.CACHE_DIR, slug]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open("%s/render.json" % dir, FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(render, "\t"))
	f.close()
