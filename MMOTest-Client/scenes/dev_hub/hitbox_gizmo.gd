class_name HitboxGizmo
extends Node3D
## 3D hitbox visualization + interactive gizmo for the NPC editor.
##
## Draggable handles:
##   translate_x / translate_y / translate_z — move the hitbox center
##   radius — scale the radius (uniform for spheres, per-side for capsules)
##   height — scale the capsule height (hidden for spheres)
##
## The gizmo lives in the SubViewport alongside the model preview. The panel
## routes mouse input to handle_input(); the gizmo emits hitbox_changed during
## drags so the panel can sync its spinboxes.

signal hitbox_changed(new_config: Dictionary)

const DEFAULT_HITBOX := {
	"type": "capsule",
	"radius": 0.4,
	"height": 1.8,
	"offset_x": 0.0,
	"offset_y": 0.9,
	"offset_z": 0.0,
}

const HITBOX_COLOR := Color(1.0, 0.2, 0.2)
const AXIS_COL_X := Color(1.0, 0.3, 0.3)
const AXIS_COL_Y := Color(0.3, 1.0, 0.3)
const AXIS_COL_Z := Color(0.3, 0.5, 1.0)
const AXIS_COL_R := Color(1.0, 0.6, 0.1)
const AXIS_COL_H := Color(0.2, 0.9, 0.9)

@export var camera: Camera3D
@export var handle_radius: float = 0.09
@export var handle_offset_from_center: float = 0.5
@export var handle_screen_click_radius: float = 20.0
@export var label_font_size: int = 28
@export var label_pixel_size: float = 0.006
@export var hitbox_capsule_alpha: float = 0.1
@export var pole_thickness: float = 0.012

var _config: Dictionary = DEFAULT_HITBOX.duplicate()
var _hitbox_visual: MeshInstance3D
var _gizmo_root: Node3D
var _handles: Array = []
var _handle_labels: Dictionary = {}
var _poles: Dictionary = {}

var _dragging: Dictionary = {}
var _drag_plane: Plane
var _drag_initial: Vector3
var _drag_initial_value: float
var _drag_initial_offset: Vector3

func _ready() -> void:
	_build_hitbox_visual()
	_gizmo_root = Node3D.new()
	add_child(_gizmo_root)
	_build_handles()
	_refresh_display()

func set_hitbox(config: Dictionary) -> void:
	_config = config.duplicate()
	_refresh_display()

func get_hitbox() -> Dictionary:
	return _config.duplicate()

func set_gizmo_visible(v: bool) -> void:
	visible = v
	_hitbox_visual.visible = v
	_gizmo_root.visible = v


# ---------- Build ----------

func _build_hitbox_visual() -> void:
	_hitbox_visual = MeshInstance3D.new()
	_hitbox_visual.layers = 1
	_hitbox_visual.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var mat := StandardMaterial3D.new()
	var c := HITBOX_COLOR
	c.a = hitbox_capsule_alpha  # alpha via material, not GeometryInstance
	mat.albedo_color = c
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	mat.no_depth_test = true
	mat.render_priority = 1
	_hitbox_visual.material_override = mat
	# NOTE: do NOT set _hitbox_visual.transparency — combined with ALPHA material
	# it renders the mesh at effectively 0 alpha in Godot 4.
	add_child(_hitbox_visual)

func _build_handles() -> void:
	_build_pole("translate_x", AXIS_COL_X)
	_build_pole("translate_y", AXIS_COL_Y)
	_build_pole("translate_z", AXIS_COL_Z)
	_build_pole("radius", AXIS_COL_R)
	_build_pole("height", AXIS_COL_H)

	_add_handle("translate_x", Vector3(1, 0, 0), AXIS_COL_X, "X")
	_add_handle("translate_y", Vector3(0, 1, 0), AXIS_COL_Y, "Y")
	_add_handle("translate_z", Vector3(0, 0, 1), AXIS_COL_Z, "Z")
	_add_handle("radius", Vector3(1, 0, 0), AXIS_COL_R, "Width")
	_add_handle("height", Vector3(0, 1, 0), AXIS_COL_H, "Height")

func _build_pole(kind: String, color: Color) -> void:
	var mi := MeshInstance3D.new()
	var box := BoxMesh.new()
	box.size = Vector3(1.0, pole_thickness, pole_thickness)
	mi.mesh = box
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.no_depth_test = true
	mi.material_override = mat
	mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_gizmo_root.add_child(mi)
	_poles[kind] = mi

func _add_handle(kind: String, axis: Vector3, color: Color, label_text: String) -> void:
	var mi := MeshInstance3D.new()
	var sphere := SphereMesh.new()
	sphere.radius = handle_radius
	sphere.height = handle_radius * 2.0
	mi.mesh = sphere
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.no_depth_test = true
	mi.material_override = mat
	mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_gizmo_root.add_child(mi)
	_handles.append({"node": mi, "kind": kind, "axis": axis})

	var lbl := Label3D.new()
	lbl.text = label_text
	lbl.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	lbl.no_depth_test = true
	lbl.font_size = label_font_size
	lbl.pixel_size = label_pixel_size
	lbl.modulate = color
	lbl.outline_modulate = Color(0, 0, 0, 1)
	lbl.outline_size = 6
	_gizmo_root.add_child(lbl)
	_handle_labels[kind] = lbl

# ---------- Refresh ----------

func _refresh_display() -> void:
	var is_capsule := str(_config.get("type", "capsule")) == "capsule"

	if is_capsule:
		var cm := CapsuleMesh.new()
		cm.radius = maxf(float(_config.get("radius", 0.4)), 0.05)
		cm.height = maxf(float(_config.get("height", 1.8)), cm.radius * 2.0 + 0.05)
		_hitbox_visual.mesh = cm
	else:
		var sm := SphereMesh.new()
		sm.radius = maxf(float(_config.get("radius", 0.4)), 0.05)
		sm.height = sm.radius * 2.0
		_hitbox_visual.mesh = sm

	var center := Vector3(
		float(_config.get("offset_x", 0.0)),
		float(_config.get("offset_y", 0.9)),
		float(_config.get("offset_z", 0.0)))
	_hitbox_visual.position = center

	var radius: float = float(_config.get("radius", 0.4))
	var half_h: float = float(_config.get("height", 1.8)) * 0.5 if is_capsule else radius
	var move_dist := maxf(radius, half_h) + handle_offset_from_center

	var positions := {}
	for h in _handles:
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
		positions[kind] = pos

	_update_pole("translate_x", center, positions["translate_x"], true)
	_update_pole("translate_y", center, positions["translate_y"], true)
	_update_pole("translate_z", center, positions["translate_z"], true)
	_update_pole("radius",      center, positions["radius"], true)
	_update_pole("height",      center, positions["height"], is_capsule)

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
			box.size = Vector3(length, pole_thickness, pole_thickness)
		"translate_y", "height":
			box.size = Vector3(pole_thickness, length, pole_thickness)
		"translate_z":
			box.size = Vector3(pole_thickness, pole_thickness, length)
	mi.position = (center + end) * 0.5

# ---------- Input ----------

func handle_input(event: InputEvent, mouse_pos: Vector2) -> bool:
	if not _gizmo_root.visible: return false

	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT:
			if mb.pressed:
				return _try_start_drag(mouse_pos)
			else:
				if not _dragging.is_empty():
					_dragging = {}
					return true
	elif event is InputEventMouseMotion and not _dragging.is_empty():
		_update_drag(mouse_pos)
		return true
	return false

func _try_start_drag(mouse: Vector2) -> bool:
	if camera == null: return false
	var best: Dictionary = {}
	var best_dist := handle_screen_click_radius
	for h in _handles:
		var node: MeshInstance3D = h.node
		if node == null or not is_instance_valid(node): continue
		if not node.visible: continue
		var screen := camera.unproject_position(node.global_position)
		var d := screen.distance_to(mouse)
		if d < best_dist:
			best_dist = d
			best = h
	if best.is_empty(): return false

	_dragging = best
	var kind: String = best.kind
	var handle_pos: Vector3 = (best.node as MeshInstance3D).global_position
	var fwd := -camera.global_transform.basis.z
	_drag_plane = Plane(fwd, handle_pos)

	var ray_origin := camera.project_ray_origin(mouse)
	var ray_dir := camera.project_ray_normal(mouse)
	var hit = _drag_plane.intersects_ray(ray_origin, ray_dir)
	if hit == null:
		_dragging = {}
		return false
	_drag_initial = hit

	if kind == "radius":
		_drag_initial_value = float(_config.get("radius", 0.4))
	elif kind == "height":
		_drag_initial_value = float(_config.get("height", 1.8))
	else:
		_drag_initial_offset = Vector3(
			float(_config.get("offset_x", 0.0)),
			float(_config.get("offset_y", 0.9)),
			float(_config.get("offset_z", 0.0)))
	return true

func _update_drag(mouse: Vector2) -> void:
	if camera == null: return
	var kind: String = _dragging.kind
	var axis: Vector3 = _dragging.axis
	var ray_origin := camera.project_ray_origin(mouse)
	var ray_dir := camera.project_ray_normal(mouse)
	var hit = _drag_plane.intersects_ray(ray_origin, ray_dir)
	if hit == null: return
	var current: Vector3 = hit
	var amount := (current - _drag_initial).dot(axis)

	match kind:
		"translate_x", "translate_y", "translate_z":
			var off: Vector3 = _drag_initial_offset + axis * amount
			_config["offset_x"] = off.x
			_config["offset_y"] = off.y
			_config["offset_z"] = off.z
		"radius":
			var nr := maxf(0.05, _drag_initial_value + amount)
			_config["radius"] = nr
			if str(_config.get("type", "capsule")) == "capsule" and float(_config.get("height", 1.8)) < nr * 2.0 + 0.05:
				_config["height"] = nr * 2.0 + 0.05
		"height":
			var nh := maxf(float(_config.get("radius", 0.4)) * 2.0 + 0.05, _drag_initial_value + amount * 2.0)
			_config["height"] = nh

	_refresh_display()
	hitbox_changed.emit(_config.duplicate())
