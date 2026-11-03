extends VBoxContainer
## Reusable NPC editor panel. Configured with `mode`: "template" or "unique".
##
## Save behavior:
##   - Save Hitbox: writes current hitbox to disk.
##   - Save: writes combat data to DB AND flushes pending hitbox edits.
##   - Reload from DB: discards unsaved hitbox edits, re-fetches DB rows, and
##     reloads hitbox from disk. Never writes.
##   - Reset Hitbox: sets hitbox to the model's default AABB and saves it
##     immediately, overwriting whatever was on disk.

@export var mode: String = "template"

@onready var search_edit: LineEdit = %SearchEdit
@onready var npc_selector: OptionButton = %NpcSelector
@onready var anim_selector: OptionButton = %AnimSelector
@onready var inspector_fields: VBoxContainer = %InspectorFields
@onready var save_button: Button = %SaveButton
@onready var reload_button: Button = %ReloadButton
@onready var sub_viewport: SubViewport = %SubViewport
@onready var camera: Camera3D = %Camera3D

# Hitbox UI
var hitbox_toggle: CheckBox
var hitbox_type_selector: OptionButton
var hitbox_radius_spin: SpinBox
var hitbox_height_spin: SpinBox
var hitbox_offset_x_spin: SpinBox
var hitbox_offset_y_spin: SpinBox
var hitbox_offset_z_spin: SpinBox
var hitbox_save_button: Button
var hitbox_reset_button: Button
var hitbox_height_label: Label

# Panel state
var _all_items: Array = []
var _filtered_items: Array = []
var _current_id: int = 0
var _current_data: Dictionary = {}
var _current_model: Node3D = null
var _current_slug: String = ""
var _reloading: bool = false

# Hitbox state
var _hitbox: Dictionary = {
	"type": "capsule",
	"radius": 0.4,
	"height": 1.8,
	"offset": Vector3(0, 0.9, 0),
}
var _hitbox_visible: bool = false
var _hitbox_dirty: bool = false

# 3D nodes
var _model_holder: Node3D
var _hitbox_visual: MeshInstance3D
var _gizmo_root: Node3D
var _gizmo_handles: Array = []
var _handle_labels: Dictionary = {}
var _poles: Dictionary = {}

# Camera orbit
var _orbit_yaw: float = 0.6
var _orbit_pitch: float = 0.35
var _orbit_distance: float = 4.0
var _orbit_target: Vector3 = Vector3(0, 1, 0)
var _orbiting: bool = false

# Drag state
var _dragging_handle: Dictionary = {}
var _drag_plane: Plane
var _drag_initial: Vector3
var _drag_initial_value: float
var _drag_initial_offset: Vector3


func _ready() -> void:
	_build_hitbox_ui()
	_build_3d_nodes()
	_connect_signals()
	_reload_items()


func _connect_signals() -> void:
	search_edit.text_changed.connect(_on_search_changed)
	npc_selector.item_selected.connect(_on_npc_selected)
	anim_selector.item_selected.connect(_on_anim_selected)
	save_button.pressed.connect(_on_save_pressed)
	reload_button.pressed.connect(_refresh_from_db)

	var container := sub_viewport.get_parent() as SubViewportContainer
	if container != null:
		container.gui_input.connect(_on_viewer_input)


func _process(_delta: float) -> void:
	_update_camera_orbit()


# ============================================================
# UI setup
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

	var sep1 := VSeparator.new()
	row1.add_child(sep1)

	var type_label := Label.new()
	type_label.text = "Type:"
	row1.add_child(type_label)

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

	_update_hitbox_controls()


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


func _build_3d_nodes() -> void:
	_model_holder = Node3D.new()
	_model_holder.name = "ModelHolder"
	sub_viewport.add_child(_model_holder)

	_hitbox_visual = MeshInstance3D.new()
	_hitbox_visual.name = "HitboxVisual"
	_hitbox_visual.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.2, 1.0, 0.2, 1.0)
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	_hitbox_visual.material_override = mat
	_hitbox_visual.transparency = 0.5
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

	_build_pole("translate_x", Color(1.0, 0.3, 0.3))
	_build_pole("translate_y", Color(0.3, 1.0, 0.3))
	_build_pole("translate_z", Color(0.3, 0.5, 1.0))
	_build_pole("radius",      Color(1.0, 0.6, 0.1))
	_build_pole("height",      Color(0.2, 0.9, 0.9))

	_add_handle("translate_x", Vector3(1, 0, 0), Color(1.0, 0.3, 0.3), "X")
	_add_handle("translate_y", Vector3(0, 1, 0), Color(0.3, 1.0, 0.3), "Y")
	_add_handle("translate_z", Vector3(0, 0, 1), Color(0.3, 0.5, 1.0), "Z")
	_add_handle("radius",      Vector3(1, 0, 0), Color(1.0, 0.6, 0.1), "Width")
	_add_handle("height",      Vector3(0, 1, 0), Color(0.2, 0.9, 0.9), "Height")


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

	var label := Label3D.new()
	label.text = label_text
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	label.no_depth_test = true
	label.font_size = 28
	label.pixel_size = 0.006
	label.modulate = color
	label.outline_modulate = Color(0, 0, 0, 1)
	label.outline_size = 6
	label.visible = false
	_gizmo_root.add_child(label)
	_handle_labels[kind] = label


# ============================================================
# Item loading
# ============================================================

func _reload_items() -> void:
	_all_items = []
	if mode == "template":
		_all_items = NpcEditorBridge.GetTemplates()
	elif mode == "unique":
		_all_items = NpcEditorBridge.GetNpcs()
	_rebuild_dropdown()


func _refresh_from_db() -> void:
	_reloading = true
	_reload_items()
	_reloading = false


func _on_search_changed(_text: String) -> void:
	_rebuild_dropdown()


func _rebuild_dropdown() -> void:
	npc_selector.clear()
	_filtered_items.clear()
	var filter := search_edit.text.strip_edges().to_lower()
	for it in _all_items:
		var label: String = str(it.get("slug_id", it.get("name", "")))
		if filter.is_empty() or label.to_lower().contains(filter):
			_filtered_items.append(it)
			npc_selector.add_item(label)
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
	if idx < 0 or idx >= _filtered_items.size():
		return
	_current_id = int(_filtered_items[idx].id)
	_load_current()


func _load_current() -> void:
	# Auto-save pending hitbox edits — UNLESS we're here because the user
	# clicked Reload, which explicitly wants those discarded.
	if not _reloading and _hitbox_dirty and _current_slug != "":
		if not _maybe_save_hitbox():
			push_warning("[NpcEditor] Auto-save hitbox failed for '%s'" % _current_slug)

	if _current_id == 0:
		_clear_all()
		return
	if mode == "template":
		_current_data = NpcEditorBridge.GetTemplate(_current_id)
		if _current_data == null: _current_data = {}
	else:
		_current_data = NpcEditorBridge.GetNpcFull(_current_id)
		if _current_data == null: _current_data = {}
	_build_inspector()
	_load_model()
	_load_hitbox_for_slug()
	_update_hitbox_display()


func _clear_all() -> void:
	_current_id = 0
	_current_data = {}
	_current_slug = ""
	_hitbox_dirty = false
	_update_hitbox_save_button()
	_clear_model()
	for c in inspector_fields.get_children():
		c.queue_free()


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

	if inst == null:
		inst = _build_fallback_model()

	_current_model = inst
	_model_holder.add_child(inst)
	_rebuild_anim_dropdown()
	_frame_camera(inst)


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

	var ap := AnimationPlayer.new()
	ap.name = "AnimationPlayer"
	root.add_child(ap)
	var lib := AnimationLibrary.new()
	var idle := Animation.new()
	idle.length = 1.0
	idle.loop_mode = Animation.LOOP_LINEAR
	lib.add_animation("idle", idle)
	ap.add_animation_library("", lib)

	return root


func _clear_model() -> void:
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
		if names[i] == "idle": idle_idx = i
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
# Camera orbit
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
	var offset := Vector3(
		_orbit_distance * cp * sy,
		_orbit_distance * sp,
		_orbit_distance * cp * cy)
	camera.global_position = _orbit_target + offset
	camera.look_at(_orbit_target, Vector3.UP)


func _compute_aabb(root: Node3D) -> AABB:
	var first := true
	var result := AABB()
	for mi in _find_mesh_instances(root):
		if mi.mesh == null: continue
		var box: AABB = mi.mesh.get_aabb()
		var world_box: AABB = mi.global_transform * box
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
# Hitbox load / save
# ============================================================

func _hitbox_path(slug: String) -> String:
	return "user://npc_render/%s/hitbox.json" % slug


func _load_hitbox_for_slug() -> void:
	_hitbox = _default_hitbox_for_model()
	if _current_slug == "":
		_hitbox_dirty = false
		_update_hitbox_controls()
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
	_hitbox_dirty = false
	_update_hitbox_controls()
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
	# Set to the model's AABB default and write immediately, overwriting any
	# existing hitbox.json for this slug.
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


# ============================================================
# Hitbox display
# ============================================================

func _set_model_transparency(t: float) -> void:
	if _current_model == null: return
	_apply_transparency_recursive(_current_model, t)


func _apply_transparency_recursive(node: Node, t: float) -> void:
	if node is GeometryInstance3D:
		(node as GeometryInstance3D).transparency = t
	for c in node.get_children():
		_apply_transparency_recursive(c, t)


func _update_hitbox_display() -> void:
	if _hitbox_visual == null: return
	_hitbox_visual.visible = _hitbox_visible
	_gizmo_root.visible = _hitbox_visible
	_set_model_transparency(0.5 if _hitbox_visible else 0.0)

	if not _hitbox_visible:
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
# Viewer input
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

	if event is InputEventMouseMotion:
		var mm := event as InputEventMouseMotion
		if _orbiting:
			_orbit_yaw -= mm.relative.x * 0.008
			_orbit_pitch = clampf(_orbit_pitch + mm.relative.y * 0.008, -1.4, 1.4)
			return
		if not _dragging_handle.is_empty():
			_update_drag(mm.position)
			return


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
# Inspector
# ============================================================

func _template_fields() -> Array:
	return [
		{"key": "slug_id", "label": "Slug ID", "type": "readonly"},
		{"key": "final_level", "label": "Final Level", "type": "int", "min": 1},
		{"key": "hitpoints", "label": "Hitpoints", "type": "int", "min": 0},
		{"key": "max_hitpoints", "label": "Max Hitpoints", "type": "int", "min": 1},
		{"key": "accuracy", "label": "Accuracy", "type": "int", "min": 1},
		{"key": "max_accuracy", "label": "Max Accuracy", "type": "int", "min": 1},
		{"key": "strength", "label": "Strength", "type": "int", "min": 1},
		{"key": "max_strength", "label": "Max Strength", "type": "int", "min": 1},
		{"key": "defence", "label": "Defence", "type": "int", "min": 1},
		{"key": "max_defence", "label": "Max Defence", "type": "int", "min": 1},
		{"key": "ether", "label": "Ether", "type": "int", "min": 1},
		{"key": "max_ether", "label": "Max Ether", "type": "int", "min": 1},
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
		{"key": "tile_x", "label": "Tile X", "type": "int"},
		{"key": "tile_y", "label": "Tile Y", "type": "int"},
		{"key": "tile_z", "label": "Tile Z", "type": "int"},
		{"key": "attackable", "label": "Attackable", "type": "bool"},
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
		{"key": "melee_bonus", "label": "Melee Bonus", "type": "int", "min": 0},
		{"key": "ranged_bonus", "label": "Ranged Bonus", "type": "int", "min": 0},
		{"key": "ether_bonus", "label": "Ether Bonus", "type": "int", "min": 0},
		{"key": "melee_defense", "label": "Melee Defense", "type": "int", "min": 0},
		{"key": "ranged_defense", "label": "Ranged Defense", "type": "int", "min": 0},
		{"key": "ether_defense", "label": "Ether Defense", "type": "int", "min": 0},
	]


func _build_inspector() -> void:
	for c in inspector_fields.get_children():
		c.queue_free()
	var fields := _unique_fields() if mode == "unique" else _template_fields()
	for spec in fields:
		if str(spec.get("type", "")) == "separator":
			var hs := HSeparator.new()
			inspector_fields.add_child(hs)
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
