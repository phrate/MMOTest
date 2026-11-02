extends Camera3D

@export_group("Target")
@export var target: Node3D
@export var target_offset := Vector3.ZERO

@export_group("Orbit")
@export_range(-89.0, 89.0, 0.1) var min_pitch_degrees := -30.0
@export_range(-89.0, 89.0, 0.1) var max_pitch_degrees := 70.0
@export var mouse_sensitivity_degrees := 0.2
@export var invert_vertical_drag := false

@export_group("Zoom")
@export_range(0.1, 100.0, 0.1) var min_distance := 2.0
@export_range(0.1, 100.0, 0.1) var max_distance := 12.0
@export var zoom_step := 0.75
@export_range(0.0, 30.0, 0.1) var zoom_smoothing := 12.0

@export_group("Defaults")
@export var default_yaw_degrees := 0.0
@export_range(-89.0, 89.0, 0.1) var default_pitch_degrees := 35.0
@export_range(0.1, 100.0, 0.1) var default_distance := 7.0
@export_range(1.0, 179.0, 0.1) var default_fov := 50.0
@export_range(1.0, 179.0, 0.1) var min_fov := 30.0
@export_range(1.0, 179.0, 0.1) var max_fov := 90.0

var _yaw := 0.0
var _pitch := 0.0
var _distance := 0.0
var _target_distance := 0.0
var _min_pitch := 0.0
var _max_pitch := 0.0
var _dragging := false


func _ready() -> void:
	if target == null:
		target = get_parent() as Node3D

	top_level = true

	_min_pitch = deg_to_rad(min_pitch_degrees)
	_max_pitch = deg_to_rad(max_pitch_degrees)
	if _min_pitch > _max_pitch:
		var tmp := _min_pitch
		_min_pitch = _max_pitch
		_max_pitch = tmp

	if min_distance > max_distance:
		var tmp := min_distance
		min_distance = max_distance
		max_distance = tmp

	if min_fov > max_fov:
		var tmp := min_fov
		min_fov = max_fov
		max_fov = tmp

	fov = clampf(default_fov, min_fov, max_fov)

	reset_camera()


## Restores yaw, pitch, and zoom to the exported defaults.
func reset_camera() -> void:
	_yaw = deg_to_rad(default_yaw_degrees)
	_pitch = clampf(deg_to_rad(default_pitch_degrees), _min_pitch, _max_pitch)
	_distance = clampf(default_distance, min_distance, max_distance)
	_target_distance = _distance
	_update_camera()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_MIDDLE:
			_dragging = event.pressed
			get_viewport().set_input_as_handled()
			return

		if event.pressed:
			if event.button_index == MOUSE_BUTTON_WHEEL_UP:
				_target_distance = clampf(
					_target_distance - zoom_step * event.factor,
					min_distance,
					max_distance
				)
				get_viewport().set_input_as_handled()
			elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
				_target_distance = clampf(
					_target_distance + zoom_step * event.factor,
					min_distance,
					max_distance
				)
				get_viewport().set_input_as_handled()

	elif event is InputEventMouseMotion and _dragging:
		_yaw -= deg_to_rad(event.relative.x * mouse_sensitivity_degrees)

		var pitch_delta := deg_to_rad(event.relative.y * mouse_sensitivity_degrees)
		if invert_vertical_drag:
			pitch_delta = -pitch_delta

		_pitch = clampf(_pitch - pitch_delta, _min_pitch, _max_pitch)
		get_viewport().set_input_as_handled()


func _process(delta: float) -> void:
	if zoom_smoothing > 0.0:
		var weight := 1.0 - exp(-zoom_smoothing * delta)
		_distance = lerpf(_distance, _target_distance, weight)
	else:
		_distance = _target_distance

	_update_camera()


func _update_camera() -> void:
	if not is_instance_valid(target):
		return

	var focus := target.global_position + target_offset

	var horizontal := cos(_pitch) * _distance
	var vertical := sin(_pitch) * _distance
	var offset := Vector3(0.0, vertical, horizontal).rotated(Vector3.UP, _yaw)

	global_position = focus + offset
	look_at(focus, Vector3.UP)


## Snap the camera to the target's current transform, bypassing any smoothing.
## Use after teleporting or respawning the target so the first rendered frame
## is already correct (avoids a one-frame flash on scene load).
func snap_now() -> void:
	_update_camera()
