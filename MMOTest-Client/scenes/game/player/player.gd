extends CharacterBody3D
## Local player. Click a tile to walk there via async A* pathfinding.
## Position is server-authoritative: click → path request → send path →
## server walks the path tile-by-tile → client interpolates received positions.

signal walk_rejected(tile: Vector2i, reason: String)

@export var move_speed := 2.0   # must equal the server's WalkSpeed
@export var camera: Camera3D

const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)

var _target_pos: Vector3
var _target_yaw: float = 0.0
var _pending_path_id: int = -1


func _ready() -> void:
	add_to_group("local_player")

	if camera == null:
		camera = get_node_or_null("Camera3D")

	global_position = Session.spawn_position
	rotation.y = Session.spawn_yaw
	_target_pos = Session.spawn_position
	_target_yaw = Session.spawn_yaw

	if camera != null and camera.has_method("snap_now"):
		camera.snap_now()

	Network.self_position_updated.connect(_on_self_position_updated)
	Pathfinder.path_found.connect(_on_path_found)
	Pathfinder.path_failed.connect(_on_path_failed)


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton \
			and event.pressed \
			and event.button_index == MOUSE_BUTTON_LEFT:
		_try_set_move_target()
		get_viewport().set_input_as_handled()


func _try_set_move_target() -> void:
	var cam := get_viewport().get_camera_3d()
	if cam == null:
		return

	var mouse_pos := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mouse_pos)
	var dir := cam.project_ray_normal(mouse_pos)

	var tile := _ray_to_tile(from, dir)
	if tile == INVALID_TILE:
		return
	_request_path_to(tile)


## Tile-aware raycast. For each integer height level from z_min to z_max, find
## where the ray crosses that horizontal plane, round to tile coords, and check
## that a tile actually exists there at that exact height. Returns the closest
## hit. Respects raised tiles: clicking a tile at z=105 hits that tile, not the
## ground below it at z=100.
func _ray_to_tile(from: Vector3, dir: Vector3) -> Vector2i:
	# OSRS camera looks down at the player. A near-horizontal or upward ray can't
	# meaningfully hit any tile top surface.
	if dir.y >= -0.01:
		return INVALID_TILE

	var best_t := INF
	var best := INVALID_TILE

	for z_level in range(World.z_min, World.z_max + 1):
		var t := (float(z_level) - from.y) / dir.y
		if t <= 0.0 or t >= best_t:
			continue
		var p := from + dir * t
		var tx := roundi(p.x)
		var ty := roundi(p.z)
		if not World.has_tile(tx, ty):
			continue
		if World.get_z(tx, ty) != z_level:
			continue
		best_t = t
		best = Vector2i(tx, ty)

	return best


func _request_path_to(tile: Vector2i) -> void:
	if not World.is_walkable(tile.x, tile.y):
		walk_rejected.emit(tile, "not_walkable")
		return
	var current_tile := Vector2i(
		roundi(global_position.x),
		roundi(global_position.z))
	_pending_path_id = Pathfinder.request_path(current_tile, tile)


func _on_path_found(request_id: int, path: Array) -> void:
	if request_id != _pending_path_id:
		return
	_pending_path_id = -1
	if path.is_empty():
		return   # already on the clicked tile
	Network.send_walk_path(path)


func _on_path_failed(request_id: int, reason: String) -> void:
	if request_id != _pending_path_id:
		return
	_pending_path_id = -1
	walk_rejected.emit(Vector2i.ZERO, reason)


func _process(delta: float) -> void:
	var to_target := _target_pos - global_position
	var horiz_sq := to_target.x * to_target.x + to_target.z * to_target.z
	# Baseline: move_speed covers horizontal distance. When the target is at a
	# different Z, scale the 3D speed so horizontal progress is still move_speed
	# and the vertical delta is closed in the same wall-clock time.
	var speed := move_speed
	if horiz_sq > 0.0001:
		var horiz_dist := sqrt(horiz_sq)
		var dist_3d := to_target.length()
		speed = move_speed * (dist_3d / horiz_dist)
	var step := speed * delta
	if to_target.length_squared() <= step * step:
		global_position = _target_pos
	else:
		global_position = global_position.move_toward(_target_pos, step)
	rotation.y = _target_yaw


func _on_self_position_updated(x: float, y: float, z: float, yaw: float) -> void:
	_target_pos = Vector3(x, y, z)
	_target_yaw = yaw
