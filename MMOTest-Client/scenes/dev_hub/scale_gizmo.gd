class_name ScaleGizmo
extends Node3D
## Three-axis scale gizmo for the Render tab. Positioned at the model's AABB
## center in world space. Each handle extends along its axis to the model's
## current half-extent; dragging changes the corresponding scale component.
##
## Drag math note: the handle sits at gizmo_center + (base_bounds[i] * scale + pad),
## but gizmo_center itself is the AABB center, which moves as scale changes.
## Net effect: for one unit of scale change, the handle moves by base_bounds[i]
## (the FULL extent, not half). So delta / base_bounds[i] is the correct divisor,
## not delta / (base_bounds[i] / 2).

signal scale_changed(new_scale: Vector3)

@export var camera: Camera3D
@export var handle_radius: float = 0.09
@export var handle_screen_click_radius: float = 20.0
@export var handle_pad: float = 0.2
@export var pole_thickness: float = 0.012
@export var label_font_size: int = 28
@export var label_pixel_size: float = 0.006

const AXIS_COL_X := Color(1.0, 0.3, 0.3)
const AXIS_COL_Y := Color(0.3, 1.0, 0.3)
const AXIS_COL_Z := Color(0.3, 0.5, 1.0)

var _scale: Vector3 = Vector3.ONE
var _base_bounds: Vector3 = Vector3.ONE
var _handles: Array = []
var _poles: Dictionary = {}
var _labels: Dictionary = {}

var _dragging: Dictionary = {}
var _drag_plane: Plane
var _drag_initial: Vector3
var _drag_initial_scale: Vector3

func _ready() -> void:
	_add_axis("x", Vector3(1, 0, 0), AXIS_COL_X, "X")
	_add_axis("y", Vector3(0, 1, 0), AXIS_COL_Y, "Y")
	_add_axis("z", Vector3(0, 0, 1), AXIS_COL_Z, "Z")
	_refresh_display()

func set_scale_and_bounds(scale: Vector3, base_bounds: Vector3) -> void:
	_scale = scale
	_base_bounds = base_bounds
	_refresh_display()

func get_gizmo_scale() -> Vector3:
	return _scale

func is_dragging() -> bool:
	return not _dragging.is_empty()

func set_gizmo_visible(v: bool) -> void:
	visible = v
	for h in _handles:
		h.node.visible = v
		_labels[h.axis].visible = v
	for k in _poles:
		_poles[k].visible = v

func handle_input(event: InputEvent, mouse_pos: Vector2) -> bool:
	if not visible: return false
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

func _add_axis(axis: String, unit: Vector3, color: Color, label_text: String) -> void:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.no_depth_test = true

	var pole := MeshInstance3D.new()
	var box := BoxMesh.new()
	box.size = Vector3(1, pole_thickness, pole_thickness)
	pole.mesh = box
	pole.material_override = mat
	pole.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(pole)
	_poles[axis] = pole

	var handle := MeshInstance3D.new()
	var sphere := SphereMesh.new()
	sphere.radius = handle_radius
	sphere.height = handle_radius * 2.0
	handle.mesh = sphere
	handle.material_override = mat.duplicate()
	handle.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(handle)
	_handles.append({"node": handle, "axis": axis, "unit": unit})

	var lbl := Label3D.new()
	lbl.text = label_text
	lbl.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	lbl.no_depth_test = true
	lbl.font_size = label_font_size
	lbl.pixel_size = label_pixel_size
	lbl.modulate = color
	lbl.outline_modulate = Color(0, 0, 0, 1)
	lbl.outline_size = 6
	add_child(lbl)
	_labels[axis] = lbl

func _refresh_display() -> void:
	if _base_bounds == Vector3.ZERO: return
	for h in _handles:
		var axis: String = h.axis
		var unit: Vector3 = h.unit
		var i := 0
		if axis == "y": i = 1
		elif axis == "z": i = 2
		var current_half := _base_bounds[i] * _scale[i] * 0.5
		var pos := unit * (current_half + handle_pad)
		h.node.position = pos
		_labels[axis].position = pos + Vector3(0, 0.15, 0)

		var pole: MeshInstance3D = _poles[axis]
		var box := pole.mesh as BoxMesh
		var length := pos.length()
		if length < 0.001:
			pole.visible = false
			continue
		match axis:
			"x": box.size = Vector3(length, pole_thickness, pole_thickness)
			"y": box.size = Vector3(pole_thickness, length, pole_thickness)
			"z": box.size = Vector3(pole_thickness, pole_thickness, length)
		pole.position = pos * 0.5
		pole.visible = visible

func _try_start_drag(mouse: Vector2) -> bool:
	if camera == null: return false
	var best: Dictionary = {}
	var best_dist := handle_screen_click_radius
	for h in _handles:
		var node: MeshInstance3D = h.node
		if not is_instance_valid(node): continue
		if not node.visible: continue
		var screen := camera.unproject_position(node.global_position)
		var d := screen.distance_to(mouse)
		if d < best_dist:
			best_dist = d
			best = h
	if best.is_empty(): return false

	_dragging = best
	var handle_pos: Vector3 = (best.node as MeshInstance3D).global_position
	var fwd := -camera.global_transform.basis.z
	_drag_plane = Plane(fwd, handle_pos)

	var ray_origin := camera.project_ray_origin(mouse)
	var ray_dir := camera.project_ray_normal(mouse)
	var hit: Variant = _drag_plane.intersects_ray(ray_origin, ray_dir)
	if hit == null:
		_dragging = {}
		return false
	var hit_vec: Vector3 = hit
	_drag_initial = hit_vec
	_drag_initial_scale = _scale
	return true

func _update_drag(mouse: Vector2) -> void:
	if camera == null: return
	var axis: String = _dragging.axis
	var unit: Vector3 = _dragging.unit
	var ray_origin := camera.project_ray_origin(mouse)
	var ray_dir := camera.project_ray_normal(mouse)
	var hit: Variant = _drag_plane.intersects_ray(ray_origin, ray_dir)
	if hit == null: return
	var hit_vec: Vector3 = hit
	var delta: float = (hit_vec - _drag_initial).dot(unit)

	var i := 0
	if axis == "y": i = 1
	elif axis == "z": i = 2

	# Divisor is base_bounds[i], not half of it. See class header for the math.
	var full_extent: float = _base_bounds[i]
	if full_extent < 0.001: return
	var delta_scale: float = delta / full_extent

	var new_scale := _drag_initial_scale
	match i:
		0: new_scale.x = maxf(0.1, _drag_initial_scale.x + delta_scale)
		1: new_scale.y = maxf(0.1, _drag_initial_scale.y + delta_scale)
		2: new_scale.z = maxf(0.1, _drag_initial_scale.z + delta_scale)
	_scale = new_scale
	_refresh_display()
	scale_changed.emit(_scale)
