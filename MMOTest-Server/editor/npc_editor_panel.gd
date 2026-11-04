extends VBoxContainer
## Reusable NPC editor panel. Configured with `mode`: "template" or "unique".
##
## Two responsibilities:
##   1. Combat/inspector editing (fields in InspectorFields; saved via NpcEditorBridge)
##   2. Hitbox authoring (controls in HitboxControls; saved to user://npc_render/<slug>/hitbox.json)

@export var mode: String = "template"

# ---------- Constants ----------
const Y_BOT_PATH := "res://models/y-bot-4.gltf"
const TARGET_HEIGHT_BY_SLUG := {
	"player": 1.8, "man": 1.8, "guard": 1.8,
	"goblin": 1.4, "chicken": 0.4,
}
const HITBOX_COLOR := Color(0.2, 1.0, 0.2)
const AXIS_COL_X  := Color(1.0, 0.3, 0.3)
const AXIS_COL_Y  := Color(0.3, 1.0, 0.3)
const AXIS_COL_Z  := Color(0.3, 0.5, 1.0)
const AXIS_COL_R  := Color(1.0, 0.6, 0.1)
const AXIS_COL_H  := Color(0.2, 0.9, 0.9)
const MODEL_TRANSPARENCY := 0.5

# ---------- @onready ----------
@onready var search_edit: LineEdit = %SearchEdit
@onready var npc_selector: OptionButton = %NpcSelector
@onready var anim_selector: OptionButton = %AnimSelector
@onready var inspector_fields: VBoxContainer = %InspectorFields
@onready var save_button: Button = %SaveButton
@onready var reload_button: Button = %ReloadButton
@onready var sub_viewport: SubViewport = %SubViewport
@onready var camera: Camera3D = %Camera3D

# ---------- Panel state ----------
var _all_items: Array = []
var _filtered_items: Array = []
var _current_id: int = 0
var _current_data: Dictionary = {}
var _current_model: Node3D = null
var _current_slug: String = ""
var _reloading: bool = false
var _fields: Dictionary = {}

# ---------- Hitbox UI controls ----------
var hitbox_toggle: CheckBox
var hitbox_type_selector: OptionButton
var hitbox_radius_spin: SpinBox
var hitbox_height_spin: SpinBox
var hitbox_height_label: Label
var hitbox_offset_x_spin: SpinBox
var hitbox_offset_y_spin: SpinBox
var hitbox_offset_z_spin: SpinBox
var hitbox_save_button: Button
var hitbox_reset_button: Button

# ---------- Hitbox state ----------
var _hitbox: Dictionary = {
	"type": "capsule",
	"radius": 0.4,
	"height": 1.8,
	"offset": Vector3(0, 0.9, 0),
}
var _hitbox_visible: bool = false
var _hitbox_dirty: bool = false

# ---------- Scale state ----------
var scale_x_spin: SpinBox
var scale_y_spin: SpinBox
var scale_z_spin: SpinBox
var scale_save_button: Button
var scale_reset_button: Button
var _user_scale: Vector3 = Vector3.ONE
var _base_fit_factor: float = 1.0
var _scale_dirty: bool = false

# ---------- Hitbox 3D nodes ----------
var _hitbox_visual: MeshInstance3D
var _gizmo_root: Node3D
var _gizmo_handles: Array = []         # [{node, kind, axis}]
var _handle_labels: Dictionary = {}    # kind -> Label3D
var _poles: Dictionary = {}            # kind -> MeshInstance3D

# ---------- Model transparency ----------
var _transparency_meshes: Array = []   # [[MeshInstance3D, [Material per surface]]]

# ---------- Camera orbit ----------
var _orbit_yaw: float = 0.6
var _orbit_pitch: float = 0.35
var _orbit_distance: float = 4.0
var _orbit_target: Vector3 = Vector3(0, 1, 0)
var _orbiting: bool = false

# ---------- Drag ----------
var _dragging_handle: Dictionary = {}
var _drag_plane: Plane
var _drag_initial: Vector3
var _drag_initial_value: float
var _drag_initial_offset: Vector3


# ============================================================
# Lifecycle
# ============================================================

func _ready() -> void:
	_build_hitbox_ui()
	_build_scale_ui()
	_build_hitbox_nodes()

	search_edit.text_changed.connect(_on_search_changed)
	npc_selector.item_selected.connect(_on_npc_selected)
	anim_selector.item_selected.connect(_on_anim_selected)
	save_button.pressed.connect(_on_save_pressed)
	reload_button.pressed.connect(_refresh_from_db)

	var container := sub_viewport.get_parent() as SubViewportContainer
	if container != null:
		container.gui_input.connect(_on_viewer_input)

	_reload_items()


func _process(_delta: float) -> void:
	_update_camera_orbit()


# ============================================================
# Item loading
# ============================================================

func _reload_items() -> void:
	_all_items = []
	if mode == "template":
		_all_items = NpcEditorBridge.GetTemplates()
	else:
		_all_items = NpcEditorBridge.GetNpcs()
	_rebuild_dropdown()


func _refresh_from_db() -> void:
	_reloading = true
	_reload_items()
	_reloading = false


func _on_search_changed(_t: String) -> void:
	_rebuild_dropdown()


func _rebuild_dropdown() -> void:
	npc_selector.clear()
	_filtered_items.clear()
	var filter := search_edit.text.strip_edges().to_lower()
	for it in _all_items:
		var lbl: String = str(it.get("slug_id", it.get("name", "")))
		if filter.is_empty() or lbl.to_lower().contains(filter):
			_filtered_items.append(it)
			npc_selector.add_item(lbl)
	if _filtered_items.is_empty():
		_clear_all()
		return
	for i in _filtered_items.size():
		if int(_filtered_items[i].id) == _current_id:
			npc_selector.select(i)
			_load_current()
			return
	npc_selector.select(0)
	_current_id = int(_filtered_items[0].id)
	_load_current()


func _on_npc_selected(idx: int) -> void:
	if idx < 0 or idx >= _filtered_items.size(): return
	_current_id = int(_filtered_items[idx].id)
	_load_current()


func _load_current() -> void:
	if not _reloading and _hitbox_dirty and _current_slug != "":
		_maybe_save_hitbox()
	if not _reloading and _scale_dirty and _current_slug != "":
		_maybe_save_scale()

	if _current_id == 0:
		_clear_all()
		return
	if mode == "template":
		_current_data = NpcEditorBridge.GetTemplate(_current_id)
	else:
		_current_data = NpcEditorBridge.GetNpcFull(_current_id)
	if _current_data == null: _current_data = {}
	_build_inspector()
	_load_model()
	_load_hitbox_for_slug()
	_update_hitbox_controls()
	_update_hitbox_display()


func _clear_all() -> void:
	_current_id = 0
	_current_data = {}
	_current_slug = ""
	_hitbox_dirty = false
	_update_hitbox_save_button()
	_clear_model()
	for c in inspector_fields.get_children(): c.queue_free()


# ============================================================
# Model viewer
# ============================================================

func _render_slug_for_current() -> String:
	if mode == "template":
		return str(_current_data.get("slug_id", "")).trim_prefix("npc_")
	var rs := str(_current_data.get("render_slug", ""))
	if rs != "": return rs
	var ts := str(_current_data.get("template_slug", ""))
	if ts != "": return ts.trim_prefix("npc_")
	var cs := str(_current_data.get("combat_slug", ""))
	var stripped := cs.trim_prefix("npc_")
	var at := stripped.find("@")
	return stripped.substr(0, at) if at >= 0 else stripped


func _load_model() -> void:
	_clear_model()
	var slug := _render_slug_for_current()
	_current_slug = slug
	var inst: Node3D = null

	if slug != "":
		var path := "res://npc_render/%s/%s.tscn" % [slug, slug]
		if ResourceLoader.exists(path):
			var scene := load(path) as PackedScene
			if scene != null:
				inst = scene.instantiate() as Node3D

	if inst == null and ResourceLoader.exists(Y_BOT_PATH):
		var scene := load(Y_BOT_PATH) as PackedScene
		if scene != null:
			inst = scene.instantiate() as Node3D

	if inst == null:
		inst = _build_fallback_model()

	_current_model = inst
	var holder := sub_viewport.get_node_or_null("ModelHolder")
	if holder == null:
		holder = Node3D.new()
		holder.name = "ModelHolder"
		sub_viewport.add_child(holder)
	holder.add_child(inst)

	# Compute the auto-fit factor once, then apply scale + anchor.
	var aabb := _compute_aabb(inst)
	if aabb.size.y > 0.001:
		var target_h: float = TARGET_HEIGHT_BY_SLUG.get(slug, 1.8)
		_base_fit_factor = target_h / aabb.size.y
	else:
		_base_fit_factor = 1.0

	# Load user scale for this slug.
	_user_scale = _load_render_scale(slug)
	scale_x_spin.set_value_no_signal(_user_scale.x)
	scale_y_spin.set_value_no_signal(_user_scale.y)
	scale_z_spin.set_value_no_signal(_user_scale.z)
	_scale_dirty = false
	_update_scale_save_button()

	_apply_scale_and_anchor()
	_rebuild_anim_dropdown()
	_frame_camera(inst)


func _apply_scale_and_anchor() -> void:
	if _current_model == null: return
	_current_model.scale = Vector3.ONE * _base_fit_factor * _user_scale
	_current_model.position = Vector3.ZERO
	var aabb := _compute_aabb(_current_model)
	_current_model.position.y -= aabb.position.y


func _build_fallback_model() -> Node3D:
	var root := Node3D.new()
	root.name = "Fallback"
	var mesh := MeshInstance3D.new()
	var cap := CapsuleMesh.new()
	cap.radius = 0.4
	cap.height = 1.8
	mesh.mesh = cap
	mesh.position = Vector3(0, 0.9, 0)
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.6, 0.6, 0.6)
	mesh.material_override = mat
	root.add_child(mesh)
	return root


func _clear_model() -> void:
	_restore_model_transparency()
	if _current_model != null and is_instance_valid(_current_model):
		_current_model.queue_free()
	_current_model = null
	anim_selector.clear()
	anim_selector.disabled = true


func _rebuild_anim_dropdown() -> void:
	anim_selector.clear()
	if _current_model == null:
		anim_selector.disabled = true
		return
	var ap := _find_anim_player(_current_model)
	if ap == null:
		anim_selector.add_item("(no animations)")
		anim_selector.disabled = true
		return
	anim_selector.disabled = false
	var names := ap.get_animation_list()
	var idle_idx := -1
	for i in names.size():
		anim_selector.add_item(names[i])
		if names[i] == "idle_1" or names[i] == "idle":
			idle_idx = i
	var pick := idle_idx if idle_idx >= 0 else (0 if names.size() > 0 else -1)
	if pick >= 0:
		anim_selector.select(pick)
		ap.play(names[pick])


func _on_anim_selected(idx: int) -> void:
	if _current_model == null or idx < 0: return
	var ap := _find_anim_player(_current_model)
	if ap == null: return
	var name := anim_selector.get_item_text(idx)
	if ap.has_animation(name):
		ap.play(name)


func _find_anim_player(node: Node) -> AnimationPlayer:
	if node is AnimationPlayer: return node
	for c in node.get_children():
		var ap := _find_anim_player(c)
		if ap != null: return ap
	return null


# ============================================================
# Camera
# ============================================================

func _frame_camera(root: Node3D) -> void:
	var aabb := _compute_aabb(root)
	if aabb.size == Vector3.ZERO:
		aabb = AABB(Vector3(-1, 0, -1), Vector3(2, 2, 2))
	_orbit_target = aabb.get_center()
	var size := maxf(aabb.size.x, maxf(aabb.size.y, aabb.size.z))
	_orbit_distance = maxf(size, 0.5) * 2.5
	_update_camera_orbit()


func _update_camera_orbit() -> void:
	if camera == null: return
	var cp := cos(_orbit_pitch)
	var sp := sin(_orbit_pitch)
	var cy := cos(_orbit_yaw)
	var sy := sin(_orbit_yaw)
	var offset := Vector3(_orbit_distance * cp * sy, _orbit_distance * sp, _orbit_distance * cp * cy)
	camera.global_position = _orbit_target + offset
	camera.look_at(_orbit_target, Vector3.UP)


func _compute_aabb(root: Node3D) -> AABB:
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


# ============================================================
# Viewer input (orbit / zoom / drag)
# ============================================================

func _on_viewer_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_orbiting = mb.pressed
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_orbit_distance = maxf(0.5, _orbit_distance * 0.9)
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_orbit_distance = minf(50.0, _orbit_distance * 1.1)
			return
		if mb.button_index == MOUSE_BUTTON_LEFT:
			if mb.pressed:
				_try_start_drag(mb.position)
			else:
				_dragging_handle = {}
			return
	elif event is InputEventMouseMotion:
		var mm := event as InputEventMouseMotion
		if _orbiting:
			_orbit_yaw -= mm.relative.x * 0.008
			_orbit_pitch = clampf(_orbit_pitch + mm.relative.y * 0.008, -1.4, 1.4)
			return
		if not _dragging_handle.is_empty():
			_update_drag(mm.position)


# ============================================================
# Hitbox UI construction
# ============================================================

func _build_hitbox_ui() -> void:
	var inspector_container := %InspectorContainer as VBoxContainer
	if inspector_container == null:
		push_warning("[NpcEditor] InspectorContainer not found; hitbox UI skipped")
		return

	var controls := VBoxContainer.new()
	controls.name = "HitboxControls"
	inspector_container.add_child(controls)
	inspector_container.move_child(controls, 0)

	var row1 := HBoxContainer.new()
	controls.add_child(row1)

	hitbox_toggle = CheckBox.new()
	hitbox_toggle.text = "Show Hitbox"
	hitbox_toggle.button_pressed = false
	row1.add_child(hitbox_toggle)

	row1.add_child(VSeparator.new())

	var tl := Label.new()
	tl.text = "Type:"
	row1.add_child(tl)

	hitbox_type_selector = OptionButton.new()
	hitbox_type_selector.add_item("Sphere", 0)
	hitbox_type_selector.add_item("Capsule", 1)
	hitbox_type_selector.select(1)
	row1.add_child(hitbox_type_selector)

	hitbox_save_button = Button.new()
	hitbox_save_button.text = "Save Hitbox"
	row1.add_child(hitbox_save_button)

	hitbox_reset_button = Button.new()
	hitbox_reset_button.text = "Reset Hitbox"
	row1.add_child(hitbox_reset_button)

	var row2 := HBoxContainer.new()
	controls.add_child(row2)

	row2.add_child(_mk_label("Radius:"))
	hitbox_radius_spin = _mk_spin(0.05, 10.0, 0.05, 0.4)
	row2.add_child(hitbox_radius_spin)

	hitbox_height_label = _mk_label("  Height:")
	row2.add_child(hitbox_height_label)
	hitbox_height_spin = _mk_spin(0.1, 10.0, 0.05, 1.8)
	row2.add_child(hitbox_height_spin)

	row2.add_child(_mk_label("  Off X:"))
	hitbox_offset_x_spin = _mk_spin(-10.0, 10.0, 0.05, 0.0)
	row2.add_child(hitbox_offset_x_spin)

	row2.add_child(_mk_label("Y:"))
	hitbox_offset_y_spin = _mk_spin(-10.0, 10.0, 0.05, 0.9)
	row2.add_child(hitbox_offset_y_spin)

	row2.add_child(_mk_label("Z:"))
	hitbox_offset_z_spin = _mk_spin(-10.0, 10.0, 0.05, 0.0)
	row2.add_child(hitbox_offset_z_spin)

	hitbox_toggle.toggled.connect(_on_hitbox_toggle)
	hitbox_type_selector.item_selected.connect(_on_hitbox_type_selected)
	hitbox_radius_spin.value_changed.connect(_on_hitbox_radius_changed)
	hitbox_height_spin.value_changed.connect(_on_hitbox_height_changed)
	hitbox_offset_x_spin.value_changed.connect(_on_hitbox_offset_changed)
	hitbox_offset_y_spin.value_changed.connect(_on_hitbox_offset_changed)
	hitbox_offset_z_spin.value_changed.connect(_on_hitbox_offset_changed)
	hitbox_save_button.pressed.connect(_on_save_hitbox_pressed)
	hitbox_reset_button.pressed.connect(_on_reset_hitbox_pressed)


func _mk_label(t: String) -> Label:
	var l := Label.new()
	l.text = t
	return l


func _mk_spin(minv: float, maxv: float, step: float, value: float) -> SpinBox:
	var s := SpinBox.new()
	s.min_value = minv
	s.max_value = maxv
	s.step = step
	s.value = value
	s.custom_minimum_size = Vector2(70, 0)
	return s


# ============================================================
# Hitbox 3D nodes
# ============================================================

func _build_hitbox_nodes() -> void:
	_hitbox_visual = MeshInstance3D.new()
	_hitbox_visual.name = "HitboxVisual"
	_hitbox_visual.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var mat := StandardMaterial3D.new()
	mat.albedo_color = HITBOX_COLOR
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	_hitbox_visual.material_override = mat
	_hitbox_visual.transparency = MODEL_TRANSPARENCY
	_hitbox_visual.visible = false
	sub_viewport.add_child(_hitbox_visual)

	_gizmo_root = Node3D.new()
	_gizmo_root.name = "GizmoRoot"
	_gizmo_root.visible = false
	sub_viewport.add_child(_gizmo_root)
	_build_gizmo_handles()


func _build_gizmo_handles() -> void:
	_gizmo_handles.clear()
	_handle_labels.clear()
	_poles.clear()

	_build_pole("translate_x", AXIS_COL_X)
	_build_pole("translate_y", AXIS_COL_Y)
	_build_pole("translate_z", AXIS_COL_Z)
	_build_pole("radius",      AXIS_COL_R)
	_build_pole("height",      AXIS_COL_H)

	_add_handle("translate_x", Vector3(1, 0, 0), AXIS_COL_X, "X")
	_add_handle("translate_y", Vector3(0, 1, 0), AXIS_COL_Y, "Y")
	_add_handle("translate_z", Vector3(0, 0, 1), AXIS_COL_Z, "Z")
	_add_handle("radius",      Vector3(1, 0, 0), AXIS_COL_R, "Width")
	_add_handle("height",      Vector3(0, 1, 0), AXIS_COL_H, "Height")


func _build_pole(kind: String, color: Color) -> void:
	var mi := MeshInstance3D.new()
	var box := BoxMesh.new()
	box.size = Vector3(1.0, 0.015, 0.015)
	mi.mesh = box
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.no_depth_test = true
	mi.material_override = mat
	mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	mi.visible = false
	_gizmo_root.add_child(mi)
	_poles[kind] = mi


func _add_handle(kind: String, axis: Vector3, color: Color, label_text: String) -> void:
	var mi := MeshInstance3D.new()
	var sphere := SphereMesh.new()
	sphere.radius = 0.09
	sphere.height = 0.18
	mi.mesh = sphere
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.no_depth_test = true
	mi.material_override = mat
	mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_gizmo_root.add_child(mi)
	_gizmo_handles.append({"node": mi, "kind": kind, "axis": axis})

	var lbl := Label3D.new()
	lbl.text = label_text
	lbl.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	lbl.no_depth_test = true
	lbl.font_size = 28
	lbl.pixel_size = 0.006
	lbl.modulate = color
	lbl.outline_modulate = Color(0, 0, 0, 1)
	lbl.outline_size = 6
	lbl.visible = false
	_gizmo_root.add_child(lbl)
	_handle_labels[kind] = lbl


# ============================================================
# Hitbox persistence
# ============================================================

func _hitbox_path(slug: String) -> String:
	return "user://npc_render/%s/hitbox.json" % slug


func _load_hitbox_for_slug() -> void:
	_hitbox = _default_hitbox_for_model()
	_hitbox_dirty = false
	if _current_slug == "":
		_update_hitbox_save_button()
		return
	var path := _hitbox_path(_current_slug)
	if FileAccess.file_exists(path):
		var f := FileAccess.open(path, FileAccess.READ)
		if f != null:
			var text := f.get_as_text()
			f.close()
			var parsed: Variant = JSON.parse_string(text)
			if typeof(parsed) == TYPE_DICTIONARY:
				var d: Dictionary = parsed
				_hitbox = {
					"type": str(d.get("type", "capsule")),
					"radius": float(d.get("radius", 0.4)),
					"height": float(d.get("height", 1.8)),
					"offset": Vector3(
						float(d.get("offset_x", 0.0)),
						float(d.get("offset_y", 0.9)),
						float(d.get("offset_z", 0.0))),
				}
	_update_hitbox_save_button()


func _default_hitbox_for_model() -> Dictionary:
	if _current_model == null:
		return {"type": "capsule", "radius": 0.4, "height": 1.8, "offset": Vector3(0, 0.9, 0)}
	var aabb := _compute_aabb(_current_model)
	if aabb.size == Vector3.ZERO:
		return {"type": "capsule", "radius": 0.4, "height": 1.8, "offset": Vector3(0, 0.9, 0)}
	var center := aabb.get_center()
	var radius := maxf(aabb.size.x, aabb.size.z) * 0.5
	var height := aabb.size.y
	return {
		"type": "capsule",
		"radius": maxf(radius, 0.1),
		"height": maxf(height, radius * 2.0 + 0.1),
		"offset": center,
	}


func _save_hitbox_to_disk() -> bool:
	if _current_slug == "":
		return false
	var dir := "user://npc_render/%s" % _current_slug
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open(_hitbox_path(_current_slug), FileAccess.WRITE)
	if f == null:
		return false
	var d := {
		"type": _hitbox.type,
		"radius": float(_hitbox.radius),
		"height": float(_hitbox.height),
		"offset_x": _hitbox.offset.x,
		"offset_y": _hitbox.offset.y,
		"offset_z": _hitbox.offset.z,
	}
	f.store_string(JSON.stringify(d, "\t"))
	f.close()
	print("[NpcEditor] Saved hitbox '%s' -> %s" % [_current_slug, _hitbox_path(_current_slug)])
	return true


func _maybe_save_hitbox() -> bool:
	if not _hitbox_dirty:
		return true
	if _current_slug == "":
		return false
	if _save_hitbox_to_disk():
		_hitbox_dirty = false
		_update_hitbox_save_button()
		return true
	return false


func _mark_hitbox_dirty() -> void:
	_hitbox_dirty = true
	_update_hitbox_save_button()


func _update_hitbox_save_button() -> void:
	if hitbox_save_button == null: return
	hitbox_save_button.text = "Save Hitbox *" if _hitbox_dirty else "Save Hitbox"


# ============================================================
# Hitbox UI handlers
# ============================================================

func _update_hitbox_controls() -> void:
	if hitbox_type_selector == null: return
	hitbox_type_selector.select(1 if str(_hitbox.type) == "capsule" else 0)
	hitbox_radius_spin.set_value_no_signal(float(_hitbox.radius))
	hitbox_height_spin.set_value_no_signal(float(_hitbox.height))
	hitbox_offset_x_spin.set_value_no_signal(_hitbox.offset.x)
	hitbox_offset_y_spin.set_value_no_signal(_hitbox.offset.y)
	hitbox_offset_z_spin.set_value_no_signal(_hitbox.offset.z)
	var is_capsule := str(_hitbox.type) == "capsule"
	hitbox_height_spin.visible = is_capsule
	hitbox_height_label.visible = is_capsule


func _on_hitbox_toggle(pressed: bool) -> void:
	_hitbox_visible = pressed
	_update_hitbox_display()


func _on_hitbox_type_selected(idx: int) -> void:
	_hitbox.type = "capsule" if idx == 1 else "sphere"
	_mark_hitbox_dirty()
	_update_hitbox_controls()
	_update_hitbox_display()


func _on_hitbox_radius_changed(v: float) -> void:
	_hitbox.radius = v
	if str(_hitbox.type) == "capsule" and _hitbox.height < v * 2.0 + 0.05:
		_hitbox.height = v * 2.0 + 0.05
		hitbox_height_spin.set_value_no_signal(_hitbox.height)
	_mark_hitbox_dirty()
	_update_hitbox_display()


func _on_hitbox_height_changed(v: float) -> void:
	_hitbox.height = v
	_mark_hitbox_dirty()
	_update_hitbox_display()


func _on_hitbox_offset_changed(_v: float) -> void:
	_hitbox.offset = Vector3(
		hitbox_offset_x_spin.value,
		hitbox_offset_y_spin.value,
		hitbox_offset_z_spin.value)
	_mark_hitbox_dirty()
	_update_hitbox_display()


func _on_save_hitbox_pressed() -> void:
	if _current_slug == "":
		hitbox_save_button.text = "No Slug"
		await get_tree().create_timer(1.2).timeout
		_update_hitbox_save_button()
		return
	if _save_hitbox_to_disk():
		_hitbox_dirty = false
		_update_hitbox_save_button()
		hitbox_save_button.text = "Saved ✓"
		await get_tree().create_timer(1.2).timeout
		_update_hitbox_save_button()
	else:
		hitbox_save_button.text = "Save Failed"
		await get_tree().create_timer(1.5).timeout
		_update_hitbox_save_button()


func _on_reset_hitbox_pressed() -> void:
	_hitbox = _default_hitbox_for_model()
	_hitbox_dirty = true
	_update_hitbox_controls()
	_update_hitbox_display()

	if _current_slug == "":
		hitbox_reset_button.text = "No Slug"
		await get_tree().create_timer(1.2).timeout
		hitbox_reset_button.text = "Reset Hitbox"
		return

	if _save_hitbox_to_disk():
		_hitbox_dirty = false
		_update_hitbox_save_button()
		hitbox_reset_button.text = "Reset ✓"
		await get_tree().create_timer(1.2).timeout
		hitbox_reset_button.text = "Reset Hitbox"
	else:
		hitbox_reset_button.text = "Reset Failed"
		await get_tree().create_timer(1.5).timeout
		hitbox_reset_button.text = "Reset Hitbox"


# ============================================================
# Hitbox display
# ============================================================

func _update_hitbox_display() -> void:
	if _hitbox_visual == null: return
	_hitbox_visual.visible = _hitbox_visible
	_gizmo_root.visible = _hitbox_visible

	if _hitbox_visible:
		_apply_model_transparency(MODEL_TRANSPARENCY)
	else:
		_restore_model_transparency()
		return

	var is_capsule := str(_hitbox.type) == "capsule"
	if is_capsule:
		var cm := CapsuleMesh.new()
		cm.radius = maxf(float(_hitbox.radius), 0.05)
		cm.height = maxf(float(_hitbox.height), cm.radius * 2.0 + 0.05)
		_hitbox_visual.mesh = cm
	else:
		var sm := SphereMesh.new()
		sm.radius = maxf(float(_hitbox.radius), 0.05)
		sm.height = sm.radius * 2.0
		_hitbox_visual.mesh = sm
	_hitbox_visual.position = _hitbox.offset

	var center: Vector3 = _hitbox.offset
	var radius: float = float(_hitbox.radius)
	var half_h: float = float(_hitbox.height) * 0.5 if is_capsule else radius
	var move_dist := maxf(radius, half_h) + 0.5

	var handle_positions := {}
	for h in _gizmo_handles:
		var kind: String = h.kind
		var node: MeshInstance3D = h.node
		var pos: Vector3 = center
		match kind:
			"translate_x": pos = center + Vector3(move_dist, 0, 0)
			"translate_y": pos = center + Vector3(0, move_dist, 0)
			"translate_z": pos = center + Vector3(0, 0, move_dist)
			"radius":      pos = center + Vector3(radius + 0.15, 0, 0)
			"height":      pos = center + Vector3(0, half_h + 0.15, 0)
		node.global_position = pos
		var show := (kind != "height" or is_capsule)
		node.visible = show
		var lbl: Label3D = _handle_labels.get(kind)
		if lbl != null:
			lbl.global_position = pos + Vector3(0, 0.2, 0)
			lbl.visible = show
		handle_positions[kind] = pos

	_update_pole("translate_x", center, handle_positions["translate_x"], true)
	_update_pole("translate_y", center, handle_positions["translate_y"], true)
	_update_pole("translate_z", center, handle_positions["translate_z"], true)
	_update_pole("radius",      center, handle_positions["radius"], true)
	_update_pole("height",      center, handle_positions["height"], is_capsule)


func _update_pole(kind: String, center: Vector3, end: Vector3, visible: bool) -> void:
	var mi: MeshInstance3D = _poles.get(kind)
	if mi == null: return
	if not visible:
		mi.visible = false
		return
	var diff := end - center
	var length := diff.length()
	if length < 0.001:
		mi.visible = false
		return
	mi.visible = true
	var box := mi.mesh as BoxMesh
	match kind:
		"translate_x", "radius":
			box.size = Vector3(length, 0.012, 0.012)
		"translate_y", "height":
			box.size = Vector3(0.012, length, 0.012)
		"translate_z":
			box.size = Vector3(0.012, 0.012, length)
	mi.position = (center + end) * 0.5


# ============================================================
# Drag handling
# ============================================================

func _try_start_drag(mouse: Vector2) -> void:
	if not _hitbox_visible or camera == null:
		return
	var best: Dictionary = {}
	var best_dist := 20.0
	for h in _gizmo_handles:
		var node: MeshInstance3D = h.node
		if node == null or not is_instance_valid(node): continue
		if not node.visible: continue
		var screen := camera.unproject_position(node.global_position)
		var d := screen.distance_to(mouse)
		if d < best_dist:
			best_dist = d
			best = h
	if best.is_empty():
		return

	_dragging_handle = best
	var kind: String = best.kind
	var handle_pos: Vector3 = (best.node as MeshInstance3D).global_position
	var fwd := -camera.global_transform.basis.z
	_drag_plane = Plane(fwd, handle_pos)
	var ray_origin := camera.project_ray_origin(mouse)
	var ray_dir := camera.project_ray_normal(mouse)
	var hit = _drag_plane.intersects_ray(ray_origin, ray_dir)
	if hit == null:
		_dragging_handle = {}
		return
	_drag_initial = hit

	match kind:
		"radius": _drag_initial_value = float(_hitbox.radius)
		"height": _drag_initial_value = float(_hitbox.height)
		_:
			_drag_initial_offset = _hitbox.offset
			_drag_initial_value = 0.0


func _update_drag(mouse: Vector2) -> void:
	var kind: String = _dragging_handle.kind
	var axis: Vector3 = _dragging_handle.axis
	var ray_origin := camera.project_ray_origin(mouse)
	var ray_dir := camera.project_ray_normal(mouse)
	var hit = _drag_plane.intersects_ray(ray_origin, ray_dir)
	if hit == null: return
	var current: Vector3 = hit
	var amount := (current - _drag_initial).dot(axis)

	match kind:
		"translate_x", "translate_y", "translate_z":
			var off: Vector3 = _drag_initial_offset + axis * amount
			_hitbox.offset = off
			hitbox_offset_x_spin.set_value_no_signal(off.x)
			hitbox_offset_y_spin.set_value_no_signal(off.y)
			hitbox_offset_z_spin.set_value_no_signal(off.z)
		"radius":
			var nr := maxf(0.05, _drag_initial_value + amount)
			_hitbox.radius = nr
			hitbox_radius_spin.set_value_no_signal(nr)
			if str(_hitbox.type) == "capsule" and _hitbox.height < nr * 2.0 + 0.05:
				_hitbox.height = nr * 2.0 + 0.05
				hitbox_height_spin.set_value_no_signal(_hitbox.height)
		"height":
			var nh := maxf(float(_hitbox.radius) * 2.0 + 0.05, _drag_initial_value + amount * 2.0)
			_hitbox.height = nh
			hitbox_height_spin.set_value_no_signal(nh)

	_mark_hitbox_dirty()
	_update_hitbox_display()


# ============================================================
# Model transparency (robust against opaque imported materials)
# ============================================================

func _apply_model_transparency(alpha: float) -> void:
	if _current_model == null: return
	if not _transparency_meshes.is_empty():
		# Already applied — just update alpha
		for entry in _transparency_meshes:
			var mi: MeshInstance3D = entry[0]
			if not is_instance_valid(mi): continue
			var mats: Array = entry[1]
			for i in mats.size():
				var mat: Material = mats[i]
				if mat is StandardMaterial3D:
					var s: StandardMaterial3D = mat
					var c: Color = s.albedo_color
					c.a = alpha
					s.albedo_color = c
		return

	for mi in _find_mesh_instances(_current_model):
		if mi.mesh == null: continue
		var mats: Array = []
		for i in mi.mesh.get_surface_count():
			var orig: Material = mi.get_active_material(i)
			if orig == null:
				orig = mi.mesh.surface_get_material(i)
			if orig == null:
				mats.append(null)
				continue
			var dup: Material = orig.duplicate()
			if dup is StandardMaterial3D:
				var s: StandardMaterial3D = dup
				s.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
				var c: Color = s.albedo_color
				c.a = alpha
				s.albedo_color = c
			mi.set_surface_override_material(i, dup)
			mats.append(dup)
		_transparency_meshes.append([mi, mats])


func _restore_model_transparency() -> void:
	for entry in _transparency_meshes:
		var mi: MeshInstance3D = entry[0]
		if not is_instance_valid(mi): continue
		var mats: Array = entry[1]
		for i in mats.size():
			mi.set_surface_override_material(i, null)
	_transparency_meshes.clear()


# ============================================================
# Inspector (data fields)
# ============================================================

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
		{"key": "template_slug", "label": "Template", "type": "readonly"},
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
		{"key": "spawn_flags", "label": "Spawn Flags (JSON)", "type": "text"},
		{"key": "is_dead", "label": "Is Dead", "type": "readonly_bool"},
		{"type": "separator"},
		{"key": "combat_slug", "label": "Combat Instance", "type": "readonly"},
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
		{"key": "poisoned", "label": "Poisoned", "type": "bool"},
		{"key": "burning", "label": "Burning", "type": "bool"},
		{"key": "frozen", "label": "Frozen", "type": "bool"},
		{"type": "separator"},
		{"key": "melee_bonus", "label": "Melee Bonus", "type": "int", "min": 0},
		{"key": "ranged_bonus", "label": "Ranged Bonus", "type": "int", "min": 0},
		{"key": "ether_bonus", "label": "Ether Bonus", "type": "int", "min": 0},
		{"key": "melee_defense", "label": "Melee Defense", "type": "int", "min": 0},
		{"key": "ranged_defense", "label": "Ranged Defense", "type": "int", "min": 0},
		{"key": "ether_defense", "label": "Ether Defense", "type": "int", "min": 0},
	]


func _build_inspector() -> void:
	for c in inspector_fields.get_children(): c.queue_free()
	_fields.clear()
	var fields := _unique_fields() if mode == "unique" else _template_fields()
	for spec in fields:
		if str(spec.get("type", "")) == "separator":
			inspector_fields.add_child(HSeparator.new())
			continue
		_add_field(spec)


func _add_field(spec: Dictionary) -> void:
	var key: String = spec["key"]
	var label: String = spec["label"]
	var type: String = spec["type"]
	var value = _current_data.get(key)

	var row := HBoxContainer.new()
	inspector_fields.add_child(row)
	var lbl := Label.new()
	lbl.text = label
	lbl.custom_minimum_size = Vector2(160, 0)
	row.add_child(lbl)

	match type:
		"readonly":
			var l := Label.new()
			l.text = str(value if value != null else "")
			l.modulate = Color(0.75, 0.75, 0.75)
			l.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			row.add_child(l)
		"readonly_bool":
			var l := Label.new()
			l.text = "yes" if bool(value) else "no"
			l.modulate = Color(0.75, 0.75, 0.75)
			l.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			row.add_child(l)
		"string":
			var le := LineEdit.new()
			le.text = str(value if value != null else "")
			le.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			le.text_changed.connect(func(t): _current_data[key] = t)
			row.add_child(le)
		"int":
			var sb := SpinBox.new()
			sb.min_value = spec.get("min", -99999)
			sb.max_value = 999999
			sb.step = 1
			sb.value = int(value if value != null else 0)
			sb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			sb.value_changed.connect(func(v): _current_data[key] = int(v))
			row.add_child(sb)
		"bool":
			var cb := CheckBox.new()
			cb.button_pressed = bool(value)
			cb.toggled.connect(func(p): _current_data[key] = p)
			row.add_child(cb)
		"text":
			var te := TextEdit.new()
			te.text = str(value if value != null else "")
			te.custom_minimum_size = Vector2(0, 60)
			te.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			te.text_changed.connect(func(): _current_data[key] = te.text)
			row.add_child(te)


func _on_save_pressed() -> void:
	_maybe_save_hitbox()
	_maybe_save_scale()

	if _current_data.is_empty(): return
	var ok := false
	if mode == "template":
		ok = NpcEditorBridge.SaveTemplate(_current_data)
	else:
		ok = NpcEditorBridge.SaveNpc(_current_data)
	if ok:
		print("[NpcEditor] Saved ", mode, " id=", _current_id)
		save_button.text = "Saved ✓"
		await get_tree().create_timer(1.5).timeout
		save_button.text = "Save"
	else:
		save_button.text = "Save Failed"
		await get_tree().create_timer(2.0).timeout
		save_button.text = "Save"


func _build_scale_ui() -> void:
	var inspector_container := %InspectorContainer as VBoxContainer
	if inspector_container == null: return

	var controls := VBoxContainer.new()
	controls.name = "ScaleControls"
	inspector_container.add_child(controls)
	# Place scale controls just below hitbox controls (index 0 → hitbox,
	# index 1 → scale). If hitbox wasn't built, this is index 0.
	inspector_container.move_child(controls, min(1, inspector_container.get_child_count() - 1))

	var row := HBoxContainer.new()
	controls.add_child(row)

	row.add_child(_mk_label("Mesh Scale:"))

	row.add_child(_mk_label("X:"))
	scale_x_spin = _mk_spin(0.1, 5.0, 0.05, 1.0)
	row.add_child(scale_x_spin)

	row.add_child(_mk_label("Y:"))
	scale_y_spin = _mk_spin(0.1, 5.0, 0.05, 1.0)
	row.add_child(scale_y_spin)

	row.add_child(_mk_label("Z:"))
	scale_z_spin = _mk_spin(0.1, 5.0, 0.05, 1.0)
	row.add_child(scale_z_spin)

	scale_save_button = Button.new()
	scale_save_button.text = "Save Scale"
	row.add_child(scale_save_button)

	scale_reset_button = Button.new()
	scale_reset_button.text = "Reset Scale"
	row.add_child(scale_reset_button)

	scale_x_spin.value_changed.connect(_on_scale_changed)
	scale_y_spin.value_changed.connect(_on_scale_changed)
	scale_z_spin.value_changed.connect(_on_scale_changed)
	scale_save_button.pressed.connect(_on_save_scale_pressed)
	scale_reset_button.pressed.connect(_on_reset_scale_pressed)


func _render_scale_path(slug: String) -> String:
	return "user://npc_render/%s/render.json" % slug


func _load_render_scale(slug: String) -> Vector3:
	if slug.is_empty(): return Vector3.ONE
	var path := _render_scale_path(slug)
	if not FileAccess.file_exists(path): return Vector3.ONE
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null: return Vector3.ONE
	var text := f.get_as_text()
	f.close()
	var parsed: Variant = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY: return Vector3.ONE
	var d: Dictionary = parsed
	return Vector3(
		float(d.get("scale_x", 1.0)),
		float(d.get("scale_y", 1.0)),
		float(d.get("scale_z", 1.0)))


func _save_render_scale(slug: String) -> bool:
	if slug.is_empty(): return false
	var dir := "user://npc_render/%s" % slug
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open(_render_scale_path(slug), FileAccess.WRITE)
	if f == null: return false
	var d := {
		"scale_x": _user_scale.x,
		"scale_y": _user_scale.y,
		"scale_z": _user_scale.z,
	}
	f.store_string(JSON.stringify(d, "\t"))
	f.close()
	print("[NpcEditor] Saved render scale '%s' -> %s" % [slug, _render_scale_path(slug)])
	return true


func _maybe_save_scale() -> bool:
	if not _scale_dirty: return true
	if _current_slug == "": return false
	if _save_render_scale(_current_slug):
		_scale_dirty = false
		_update_scale_save_button()
		return true
	return false


func _mark_scale_dirty() -> void:
	_scale_dirty = true
	_update_scale_save_button()


func _update_scale_save_button() -> void:
	if scale_save_button == null: return
	scale_save_button.text = "Save Scale *" if _scale_dirty else "Save Scale"


func _on_scale_changed(_v: float) -> void:
	_user_scale = Vector3(
		scale_x_spin.value,
		scale_y_spin.value,
		scale_z_spin.value)
	_apply_scale_and_anchor()
	_mark_scale_dirty()


func _on_save_scale_pressed() -> void:
	if _current_slug == "":
		scale_save_button.text = "No Slug"
		await get_tree().create_timer(1.2).timeout
		_update_scale_save_button()
		return
	if _save_render_scale(_current_slug):
		_scale_dirty = false
		_update_scale_save_button()
		scale_save_button.text = "Saved ✓"
		await get_tree().create_timer(1.2).timeout
		_update_scale_save_button()
	else:
		scale_save_button.text = "Save Failed"
		await get_tree().create_timer(1.5).timeout
		_update_scale_save_button()


func _on_reset_scale_pressed() -> void:
	_user_scale = Vector3.ONE
	scale_x_spin.set_value_no_signal(1.0)
	scale_y_spin.set_value_no_signal(1.0)
	scale_z_spin.set_value_no_signal(1.0)
	_apply_scale_and_anchor()
	_scale_dirty = true
	if _current_slug == "":
		scale_reset_button.text = "No Slug"
		await get_tree().create_timer(1.2).timeout
		scale_reset_button.text = "Reset Scale"
		return
	if _save_render_scale(_current_slug):
		_scale_dirty = false
		_update_scale_save_button()
		scale_reset_button.text = "Reset ✓"
		await get_tree().create_timer(1.2).timeout
		scale_reset_button.text = "Reset Scale"
