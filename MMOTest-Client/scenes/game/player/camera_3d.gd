extends Camera3D
## Orbit camera. Targets a CameraFocus marker if present (child of target),
## otherwise the target node itself.
##
## Y stability: small Y oscillations (animation bobbing) are ignored. Real Y
## changes (going up stairs, teleports) above `y_stability_threshold` are
## tracked with smoothing. X/Z always track directly.

@export_group("Target")
@export var target: Node3D
@export var target_offset := Vector3.ZERO
@export var focus_smoothing := 0.0

@export_group("Y Stability")
## Y changes larger than this are treated as real (stairs, teleport).
## Below this threshold, Y is held steady — absorbs animation bob.
@export var y_stability_threshold := 0.4
## Speed to adopt real Y changes (higher = snappier).
@export var y_stability_smoothing := 8.0

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
var _focus_node: Node3D = null
var _smooth_focus := Vector3.ZERO
var _focus_initialized := false

# Y stability state
var _stable_y := 0.0
var _stable_y_target := 0.0
var _y_initialized := false


func _ready() -> void:
	if target == null:
		target = get_parent() as Node3D
	top_level = true

	_min_pitch = deg_to_rad(min_pitch_degrees)
	_max_pitch = deg_to_rad(max_pitch_degrees)
	if _min_pitch > _max_pitch:
		var tmp := _min_pitch; _min_pitch = _max_pitch; _max_pitch = tmp

	if min_distance > max_distance:
		var tmp := min_distance; min_distance = max_distance; max_distance = tmp

	if min_fov > max_fov:
		var tmp := min_fov; min_fov = max_fov; max_fov = tmp

	fov = clampf(default_fov, min_fov, max_fov)
	reset_camera()
	_try_resolve_focus()


func reset_camera() -> void:
	_yaw = deg_to_rad(default_yaw_degrees)
	_pitch = clampf(deg_to_rad(default_pitch_degrees), _min_pitch, _max_pitch)
	_distance = clampf(default_distance, min_distance, max_distance)
	_target_distance = _distance
	_update_camera()


func snap_now() -> void:
	_focus_initialized = false
	_y_initialized = false
	_update_camera()


func _try_resolve_focus() -> void:
	if target == null or not is_instance_valid(target): return
	var focus_child := target.get_node_or_null("CameraFocus") as Node3D
	if focus_child != null:
		_focus_node = focus_child


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_MIDDLE:
			_dragging = event.pressed
			get_viewport().set_input_as_handled()
			return
		if event.pressed:
			if event.button_index == MOUSE_BUTTON_WHEEL_UP:
				_target_distance = clampf(_target_distance - zoom_step * event.factor, min_distance, max_distance)
				get_viewport().set_input_as_handled()
			elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
				_target_distance = clampf(_target_distance + zoom_step * event.factor, min_distance, max_distance)
				get_viewport().set_input_as_handled()

	elif event is InputEventMouseMotion and _dragging:
		_yaw -= deg_to_rad(event.relative.x * mouse_sensitivity_degrees)
		var pitch_delta := deg_to_rad(event.relative.y * mouse_sensitivity_degrees)
		if invert_vertical_drag: pitch_delta = -pitch_delta
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
	if not is_instance_valid(target): return

	if _focus_node == null or not is_instance_valid(_focus_node):
		_try_resolve_focus()

	var raw_focus: Vector3 = (
		_focus_node.global_position if _focus_node != null
		else target.global_position) + target_offset

	# --- Y stability filter ---
	# Initialize on first frame, then ignore small oscillations while adopting
	# large changes (real height transitions) with smoothing.
	var raw_y := raw_focus.y
	if not _y_initialized:
		_stable_y = raw_y
		_stable_y_target = raw_y
		_y_initialized = true
	else:
		# A change larger than threshold means we've actually moved in Y.
		if absf(raw_y - _stable_y_target) > y_stability_threshold:
			_stable_y_target = raw_y
		# Smooth toward target. If smoothing is 0, snap.
		if y_stability_smoothing > 0.0:
			var w := 1.0 - exp(-y_stability_smoothing * get_process_delta_time())
			_stable_y = lerpf(_stable_y, _stable_y_target, w)
		else:
			_stable_y = _stable_y_target

	# --- X/Z focus with optional smoothing ---
	var focus_xz := Vector2(raw_focus.x, raw_focus.z)
	var smooth_xz: Vector2
	if focus_smoothing <= 0.0:
		smooth_xz = focus_xz
	else:
		if not _focus_initialized:
			_smooth_focus = raw_focus
			_focus_initialized = true
		var t := 1.0 - exp(-focus_smoothing * get_process_delta_time())
		_smooth_focus.x = lerpf(_smooth_focus.x, focus_xz.x, t)
		_smooth_focus.z = lerpf(_smooth_focus.z, focus_xz.y, t)
		smooth_xz = Vector2(_smooth_focus.x, _smooth_focus.z)

	var focus := Vector3(smooth_xz.x, _stable_y, smooth_xz.y)

	var horizontal := cos(_pitch) * _distance
	var vertical := sin(_pitch) * _distance
	var offset := Vector3(0.0, vertical, horizontal).rotated(Vector3.UP, _yaw)
	global_position = focus + offset
	look_at(focus, Vector3.UP)
