extends CharacterBody3D

signal walk_rejected(tile: Vector2i, reason: String)

const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)
const CHAT_BUBBLE_SECONDS := 5.0
const PLAYER_HIT_RADIUS := 0.6
const PLAYER_HIT_Y_OFFSET := 1.0

@export var move_speed := 2.0
@export var camera: Camera3D

var _player_id: int = 0

var _target_pos: Vector3
var _target_yaw: float = 0.0
var _pending_path_id: int = -1
var _last_request_id: int = -1
var _last_path_length: int = 0
var _chat_hide_at_ms: int = 0

var _context_menu: PopupMenu = null
var _context_tile: Vector2i = INVALID_TILE
var _context_actions: Array = []
var _context_target_id: int = 0

@onready var name_label: Label3D = get_node_or_null("NameLabel") as Label3D
@onready var chat_label: Label3D = _resolve_chat_label()


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

	set_username(Session.username)
	Session.changed.connect(func() -> void: set_username(Session.username))

	Network.self_position_updated.connect(_on_self_position_updated)
	Pathfinder.path_found.connect(_on_path_found)
	Pathfinder.path_failed.connect(_on_path_failed)


func get_entity_type() -> String:
	return "player"


func _resolve_chat_label() -> Label3D:
	var existing := get_node_or_null("ChatLabel") as Label3D
	if existing != null:
		existing.visible = false
		return existing
	var new_label := Label3D.new()
	new_label.name = "ChatLabel"
	new_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	new_label.no_depth_test = true
	new_label.font_size = 24
	new_label.pixel_size = 0.005
	new_label.outline_modulate = Color(0, 0, 0, 1)
	new_label.outline_size = 4
	new_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	new_label.width = 500
	new_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	new_label.position = Vector3(0, 1.9, 0)
	new_label.visible = false
	add_child(new_label)
	return new_label


func set_username(username: String) -> void:
	if name_label == null: return
	name_label.text = username
	name_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	name_label.no_depth_test = true
	if name_label.position.y < 0.5:
		name_label.position.y = 2.2


func show_chat_message(text: String) -> void:
	if chat_label == null: return
	chat_label.text = text
	chat_label.visible = true
	_chat_hide_at_ms = Time.get_ticks_msec() + int(CHAT_BUBBLE_SECONDS * 1000.0)


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_LEFT:
			_try_set_move_target()
			get_viewport().set_input_as_handled()
		elif event.button_index == MOUSE_BUTTON_RIGHT:
			_try_right_click()
			get_viewport().set_input_as_handled()


func _try_set_move_target() -> void:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return
	var mouse_pos := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mouse_pos)
	var dir := cam.project_ray_normal(mouse_pos)
	var tile := _ray_to_tile(from, dir)
	if tile == INVALID_TILE: return
	_request_path_to(tile)


## Public hover helper. Returns the tile under the cursor, or INVALID_TILE.
## Uses the same ray-march as _try_set_move_target so the highlighted tile
## always matches the tile a click would target.
func get_tile_under_mouse() -> Vector2i:
	var cam := get_viewport().get_camera_3d()
	if cam == null:
		return INVALID_TILE
	var mouse_pos := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mouse_pos)
	var dir := cam.project_ray_normal(mouse_pos)
	return _ray_to_tile(from, dir)


func _try_right_click() -> void:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return
	var mouse_pos := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mouse_pos)
	var dir := cam.project_ray_normal(mouse_pos)

	var tile_hit := _ray_to_tile_with_t(from, dir)
	var player_hit := _find_player_under_ray(from, dir)
	if player_hit.node == null: return
	if player_hit.t > tile_hit.t: return

	var entity_type := "player"
	if player_hit.node.has_method("get_entity_type"):
		entity_type = player_hit.node.get_entity_type()
	if not Tooltips.has_tooltip_menu(entity_type): return

	var target_id: int = 0
	if player_hit.node.has_method("get_player_id"):
		target_id = player_hit.node.get_player_id()

	var target_username: String = ""
	if "username" in player_hit.node:
		target_username = str(player_hit.node.username)

	var system_vars := {
		"SELF_ID": Session.player_id,
		"PLAYER_ID": target_id,
		"PLAYER_NAME": target_username,
	}
	var actions: Array = Tooltips.build_actions(entity_type, system_vars)

	var tile_under := Vector2i(
		roundi(player_hit.node.global_position.x),
		roundi(player_hit.node.global_position.z))
	_show_context_menu(tile_under, target_id, actions, mouse_pos)


func _show_context_menu(tile: Vector2i, target_id: int, actions: Array, mouse_pos: Vector2) -> void:
	var menu := _ensure_context_menu()
	menu.clear()
	_context_tile = tile
	_context_target_id = target_id
	_context_actions = actions

	menu.add_item("Walk here", 0)

	if not actions.is_empty():
		menu.add_separator()
		var id := 1
		for action in actions:
			menu.add_item(str(action.get("display", action.get("id", "?"))), id)
			id += 1

	menu.position = Vector2i(mouse_pos)
	menu.popup()


func _on_context_menu_selected(id: int) -> void:
	if id == 0:
		if _context_tile != INVALID_TILE:
			_request_path_to(_context_tile)
		return
	var idx := id - 1
	if idx < 0 or idx >= _context_actions.size(): return
	var action: Dictionary = _context_actions[idx]
	var script_path: String = str(action.get("script", ""))
	if script_path.is_empty(): return
	
	Network.run_action(script_path, action.get("args", {}), _context_target_id)


func _find_player_under_ray(from: Vector3, dir: Vector3) -> Dictionary:
	var best := {"node": null, "t": INF}
	for node in get_tree().get_nodes_in_group("remote_player"):
		if not is_instance_valid(node): continue
		var center: Vector3 = node.global_position + Vector3(0, PLAYER_HIT_Y_OFFSET, 0)
		var t := _ray_sphere(from, dir, center, PLAYER_HIT_RADIUS)
		if t > 0.0 and t < best.t:
			best.node = node
			best.t = t
	return best


## Reverse-lookup: given a remote_player node, return its player_id.
## Returns 0 if the node isn't a known remote.
func get_player_id() -> int:
	return _player_id


func _ray_sphere(origin: Vector3, dir: Vector3, center: Vector3, radius: float) -> float:
	var to_center := center - origin
	var proj := to_center.dot(dir)
	if proj < 0.0: return -1.0
	var closest := origin + dir * proj
	var dist_sq := (closest - center).length_squared()
	if dist_sq > radius * radius: return -1.0
	var half_chord := sqrt(radius * radius - dist_sq)
	return proj - half_chord


func _ensure_context_menu() -> PopupMenu:
	if _context_menu != null and is_instance_valid(_context_menu):
		return _context_menu
	_context_menu = PopupMenu.new()
	_context_menu.name = "ContextMenu"
	add_child(_context_menu)
	_context_menu.id_pressed.connect(_on_context_menu_selected)
	return _context_menu


func _request_path_to(tile: Vector2i) -> void:
	var current_tile := Vector2i(
		roundi(global_position.x),
		roundi(global_position.z))
	_pending_path_id = Pathfinder.request_path(current_tile, tile)
	_last_request_id = _pending_path_id
	_last_path_length = 0


func _on_path_found(request_id: int, path: Array) -> void:
	if request_id != _pending_path_id: return
	_pending_path_id = -1
	_last_path_length = path.size()
	if path.is_empty(): return
	Network.send_walk_path(path)


func _on_path_failed(request_id: int, reason: String) -> void:
	if request_id != _pending_path_id: return
	_pending_path_id = -1
	_last_path_length = 0
	walk_rejected.emit(Vector2i.ZERO, reason)


func _process(delta: float) -> void:
	if _chat_hide_at_ms > 0 and Time.get_ticks_msec() >= _chat_hide_at_ms:
		if chat_label != null:
			chat_label.visible = false
		_chat_hide_at_ms = 0

	var to_target := _target_pos - global_position
	var horiz_sq := to_target.x * to_target.x + to_target.z * to_target.z
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


func get_debug_info() -> Dictionary:
	var ct := Vector2i(roundi(global_position.x), roundi(global_position.z))
	var tt := Vector2i(roundi(_target_pos.x), roundi(_target_pos.z))
	var state: String
	if _pending_path_id != -1: state = "requesting"
	elif global_position.distance_squared_to(_target_pos) > 0.01: state = "walking"
	else: state = "idle"
	return {
		"current_tile": ct, "current_z": World.get_z(ct.x, ct.y),
		"target_tile": tt, "target_z": World.get_z(tt.x, tt.y),
		"state": state, "path_length": _last_path_length,
		"request_id": _last_request_id,
	}


# ---------- Ray-march ----------

func _ray_to_tile(from: Vector3, dir: Vector3) -> Vector2i:
	return _ray_to_tile_with_t(from, dir).tile


func _ray_to_tile_with_t(from: Vector3, dir: Vector3) -> Dictionary:
	if dir.y >= -0.01:
		return {"tile": INVALID_TILE, "t": INF}
	var t_max := (float(World.z_min) - from.y) / dir.y
	if t_max <= 0.0:
		return {"tile": INVALID_TILE, "t": INF}
	var horiz_mag := sqrt(dir.x * dir.x + dir.z * dir.z)
	if horiz_mag > 0.001:
		t_max = minf(t_max, 300.0 / horiz_mag)

	var candidates := {}
	var steps := maxi(2, int(horiz_mag * t_max) + 2)
	for i in range(steps + 1):
		var s := float(i) / float(steps) * t_max
		var tx := roundi(from.x + dir.x * s)
		var ty := roundi(from.z + dir.z * s)
		for dx in range(-1, 2):
			for dy in range(-1, 2):
				candidates[Vector2i(tx + dx, ty + dy)] = true

	var best_t := INF
	var best := INVALID_TILE
	for tile in candidates:
		var tx: int = tile.x
		var ty: int = tile.y
		if not World.has_tile(tx, ty): continue
		var z := World.get_z(tx, ty)
		var t_top := _ray_top_face(from, dir, tx, ty, z)
		if t_top > 0.0 and t_top <= t_max and t_top < best_t:
			best_t = t_top; best = tile
		var dirs: Array[Vector2i] = [Vector2i(1,0), Vector2i(-1,0), Vector2i(0,1), Vector2i(0,-1)]
		for d in dirs:
			var nz_val := World.get_z(tx + d.x, ty + d.y)
			if nz_val >= z: continue
			var t_wall := _ray_wall_face(from, dir, tx, ty, d, z, nz_val)
			if t_wall > 0.0 and t_wall <= t_max and t_wall < best_t:
				best_t = t_wall; best = tile
	return {"tile": best, "t": best_t}


func _ray_top_face(from: Vector3, dir: Vector3, tx: int, ty: int, z: int) -> float:
	if absf(dir.y) < 0.0001: return -1.0
	var t := (float(z) - from.y) / dir.y
	if t <= 0.0: return -1.0
	var px := from.x + dir.x * t
	var pz := from.z + dir.z * t
	if px < tx - 0.5 or px > tx + 0.5: return -1.0
	if pz < ty - 0.5 or pz > ty + 0.5: return -1.0
	return t


func _ray_wall_face(from: Vector3, dir: Vector3, tx: int, ty: int, d: Vector2i, hi_z: int, lo_z: int) -> float:
	var t := -1.0
	var iy := 0.0
	if d.x != 0:
		if absf(dir.x) < 0.0001: return -1.0
		var face_x := (tx + 0.5) if d.x > 0 else (tx - 0.5)
		t = (face_x - from.x) / dir.x
		if t <= 0.0: return -1.0
		iy = from.y + dir.y * t
		var iz := from.z + dir.z * t
		if iz < ty - 0.5 or iz > ty + 0.5: return -1.0
	elif d.y != 0:
		if absf(dir.z) < 0.0001: return -1.0
		var face_z := (ty + 0.5) if d.y > 0 else (ty - 0.5)
		t = (face_z - from.z) / dir.z
		if t <= 0.0: return -1.0
		iy = from.y + dir.y * t
		var ix := from.x + dir.x * t
		if ix < tx - 0.5 or ix > tx + 0.5: return -1.0
	else:
		return -1.0
	if iy < lo_z or iy > hi_z: return -1.0
	var n := Vector3(d.x, 0.0, d.y)
	if dir.dot(n) >= 0.0: return -1.0
	return t
