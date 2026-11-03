extends CharacterBody3D
## Local player. Loads the "player" render scene + hitbox from NpcRender,
## same pipeline as NPCs, so visual and hitbox are swappable via the server.

signal walk_rejected(tile: Vector2i, reason: String)

const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)
const CHAT_BUBBLE_SECONDS := 5.0
const MOVING_THRESHOLD_SQ := 0.0001
const RENDER_SLUG := "player"

@export var move_speed := 2.0
@export var camera: Camera3D

var _target_pos: Vector3
var _target_yaw: float = 0.0
var _pending_path_id: int = -1
var _last_request_id: int = -1
var _last_path_length: int = 0
var _chat_hide_at_ms: int = 0
var _current_anim: String = ""
var _scripted_anim: String = ""
var _scripted_end_ms: int = 0
var _last_frame_pos := Vector3.ZERO
var _frame_vel := 0.0

var _render_scene_instance: Node3D = null
var _render_anim_player: AnimationPlayer = null
var _hitbox_is_fallback: bool = false

var _context_menu: PopupMenu = null
var _context_target_id: int = 0
var _context_actions: Array = []

@onready var name_label: Label3D = get_node_or_null("NameLabel") as Label3D
@onready var chat_label: Label3D = _resolve_chat_label()
@onready var anim_player: AnimationPlayer = _ensure_anim_player()


func _ready() -> void:
	add_to_group("local_player")
	if camera == null:
		camera = get_node_or_null("Camera3D")
	global_position = Session.spawn_position
	global_position.y = float(World.get_z(roundi(global_position.x), roundi(global_position.z)))
	rotation.y = Session.spawn_yaw
	_target_pos = Session.spawn_position
	_target_yaw = Session.spawn_yaw
	_last_frame_pos = global_position
	if camera != null and camera.has_method("snap_now"):
		camera.snap_now()
	set_username(Session.username)
	Session.changed.connect(func() -> void: set_username(Session.username))
	Network.self_position_updated.connect(_on_self_position_updated)
	Pathfinder.path_found.connect(_on_path_found)
	Pathfinder.path_failed.connect(_on_path_failed)
	NpcRender.slug_ready.connect(_on_render_slug_ready)
	_apply_render_scene()


# ---------- Render scene ----------

func _on_render_slug_ready(slug: String) -> void:
	if slug != RENDER_SLUG: return
	_clear_render_scene()
	_apply_render_scene()


func _clear_render_scene() -> void:
	if _render_scene_instance != null and is_instance_valid(_render_scene_instance):
		_render_scene_instance.queue_free()
		_render_scene_instance = null
	_render_anim_player = null
	var hb := get_node_or_null("Hitbox")
	if hb != null:
		remove_child(hb)
		hb.queue_free()


func _apply_render_scene() -> void:
	var default_mesh := get_node_or_null("Mesh") as Node3D
	var scene := NpcRender.get_scene(RENDER_SLUG)

	if scene != null:
		if default_mesh != null: default_mesh.visible = false
		var inst := scene.instantiate()
		if inst is Node3D:
			inst.name = "RenderScene"
			add_child(inst)
			_render_scene_instance = inst
			_render_anim_player = _find_anim_player(inst)
			if _render_anim_player == null:
				var ap := AnimationPlayer.new()
				ap.name = "AnimationPlayer"
				inst.add_child(ap)
				_create_placeholder_anims(ap)
				_render_anim_player = ap
			else:
				_create_placeholder_anims(_render_anim_player)
		else:
			inst.queue_free()
			if default_mesh != null: default_mesh.visible = true
	else:
		if default_mesh != null: default_mesh.visible = true

	var editor_hb := NpcRender.get_hitbox(RENDER_SLUG)
	if not editor_hb.is_empty():
		_attach_editor_hitbox(editor_hb)
		return

	if _render_scene_instance != null:
		var bodies := _find_all_collision_objects(_render_scene_instance)
		if not bodies.is_empty():
			for b in bodies:
				b.collision_layer = Hitboxes.HITBOX_COLLISION_LAYER
				b.collision_mask = 0
			_hitbox_is_fallback = false
			return

	_ensure_hitbox()


func _attach_editor_hitbox(hb: Dictionary) -> void:
	var spec := {
		"type": str(hb.get("type", "capsule")),
		"radius": float(hb.get("radius", 0.4)),
		"height": float(hb.get("height", 1.8)),
		"offset_x": float(hb.get("offset_x", 0.0)),
		"offset_y": float(hb.get("offset_y", 0.9)),
		"offset_z": float(hb.get("offset_z", 0.0)),
	}
	Hitboxes.attach_to(self, spec)
	_hitbox_is_fallback = false


func _find_all_collision_objects(root: Node) -> Array:
	var out := []
	for child in root.get_children():
		if child is CollisionObject3D:
			out.append(child)
		out.append_array(_find_all_collision_objects(child))
	return out


func _ensure_hitbox() -> void:
	var existing := get_node_or_null("Hitbox")
	if existing != null:
		remove_child(existing)
		existing.queue_free()
	Hitboxes.attach_to(self, Hitboxes.resolve("player", ""))
	_hitbox_is_fallback = true


# ---------- Camera / entity API ----------

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


# ---------- Input ----------

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


func _try_right_click() -> void:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return
	var mouse_pos := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mouse_pos)
	var dir := cam.project_ray_normal(mouse_pos)

	var tile_hit := _ray_to_tile_with_t(from, dir)
	var entity_hit := _find_entity_under_ray(from, dir)
	if entity_hit.node == null: return
	if entity_hit.t > tile_hit.t: return

	var node: Node3D = entity_hit.node
	var entity_type := "player"
	if node.has_method("get_entity_type"):
		entity_type = node.get_entity_type()
	if not Tooltips.has_tooltip_menu(entity_type): return

	var target_id := 0
	var system_vars := {}

	if entity_type == "npc":
		target_id = node.get_npc_id()
		var npc_data: Dictionary = Npcs.get_npc(target_id)
		system_vars = {
			"SELF_ID": Session.player_id,
			"NPC_ID": target_id,
			"NPC_DISPLAY_NAME": str(npc_data.get("display", "")),
			"NPC_COMBAT_FINAL_LEVEL": int(npc_data.get("final_level", 0)),
		}
	else:
		target_id = node.get_player_id()
		var target_username := ""
		if "username" in node:
			target_username = str(node.username)
		system_vars = {
			"SELF_ID": Session.player_id,
			"PLAYER_ID": target_id,
			"PLAYER_NAME": target_username,
		}

	var actions: Array = Tooltips.build_actions(entity_type, system_vars)
	_show_context_menu(target_id, actions, mouse_pos)


func _find_entity_under_ray(from: Vector3, dir: Vector3) -> Dictionary:
	var space := get_world_3d().direct_space_state
	var query := PhysicsRayQueryParameters3D.create(
		from, from + dir * 300.0, Hitboxes.HITBOX_COLLISION_LAYER)
	query.collide_with_areas = false
	query.collide_with_bodies = true
	var hit := space.intersect_ray(query)
	if hit.is_empty():
		return {"node": null, "t": INF}
	var body = hit.collider
	if not (body is StaticBody3D):
		return {"node": null, "t": INF}
	var entity: Node3D = body.get_parent()
	if entity == null or entity == self:
		return {"node": null, "t": INF}
	return {"node": entity, "t": from.distance_to(hit.position)}


func _show_context_menu(target_id: int, actions: Array, mouse_pos: Vector2) -> void:
	if actions.is_empty(): return
	var menu := _ensure_context_menu()
	menu.clear()
	_context_target_id = target_id
	_context_actions = actions
	var id := 0
	for action in actions:
		menu.add_item(str(action.get("display", action.get("id", "?"))), id)
		id += 1
	menu.position = Vector2i(mouse_pos)
	menu.popup()


func _ensure_context_menu() -> PopupMenu:
	if _context_menu != null and is_instance_valid(_context_menu):
		return _context_menu
	_context_menu = PopupMenu.new()
	_context_menu.name = "ContextMenu"
	add_child(_context_menu)
	_context_menu.id_pressed.connect(_on_context_menu_selected)
	return _context_menu


func _on_context_menu_selected(id: int) -> void:
	if id < 0 or id >= _context_actions.size(): return
	var action: Dictionary = _context_actions[id]
	var script_path: String = str(action.get("script", ""))
	if script_path.is_empty(): return
	Network.run_action(script_path, action.get("args", {}), _context_target_id)


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


# ---------- Movement ----------

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

	var frame_dist := global_position.distance_to(_last_frame_pos)
	_last_frame_pos = global_position
	var inst_vel := frame_dist / delta if delta > 0.0 else 0.0
	_frame_vel = lerpf(_frame_vel, inst_vel, 1.0 - exp(-15.0 * delta))

	_update_anim()


func _on_self_position_updated(x: float, y: float, z: float, yaw: float) -> void:
	_target_pos = Vector3(x, y, z)
	_target_yaw = yaw


# ---------- Animation ----------

func _current_anim_player() -> AnimationPlayer:
	if _render_anim_player != null and is_instance_valid(_render_anim_player):
		return _render_anim_player
	return anim_player


func _find_anim_player(node: Node) -> AnimationPlayer:
	if node is AnimationPlayer: return node
	for c in node.get_children():
		var ap := _find_anim_player(c)
		if ap != null: return ap
	return null


func _ensure_anim_player() -> AnimationPlayer:
	var existing := get_node_or_null("AnimationPlayer") as AnimationPlayer
	if existing != null:
		return existing
	var ap := AnimationPlayer.new()
	ap.name = "AnimationPlayer"
	add_child(ap)
	_create_placeholder_anims(ap)
	return ap


func _create_placeholder_anims(ap: AnimationPlayer) -> void:
	var lib := AnimationLibrary.new()
	if not ap.has_animation("idle"):
		var idle := Animation.new()
		idle.length = 1.0
		idle.loop_mode = Animation.LOOP_LINEAR
		lib.add_animation("idle", idle)
	if not ap.has_animation("walk"):
		var walk := Animation.new()
		walk.length = 0.4
		walk.loop_mode = Animation.LOOP_LINEAR
		var track := walk.add_track(Animation.TYPE_VALUE)
		walk.track_set_path(track, "Mesh:position")
		walk.track_insert_key(track, 0.0, Vector3(0, 0.9, 0))
		walk.track_insert_key(track, 0.2, Vector3(0, 1.0, 0))
		walk.track_insert_key(track, 0.4, Vector3(0, 0.9, 0))
		lib.add_animation("walk", walk)
	if lib.get_animation_list().size() > 0:
		ap.add_animation_library("", lib)


func _update_anim() -> void:
	var desired: String
	var now := Time.get_ticks_msec()
	if _scripted_anim != "":
		if now < _scripted_end_ms:
			desired = _scripted_anim
		else:
			_scripted_anim = ""
			desired = _derived_anim()
	else:
		desired = _derived_anim()
	if desired == _current_anim: return
	_current_anim = desired
	var ap := _current_anim_player()
	if ap != null and ap.has_animation(desired):
		ap.play(desired)


func _derived_anim() -> String:
	return "walk" if _frame_vel > 0.5 else "idle"


# ---------- Debug / ray ----------

func get_tile_under_mouse() -> Vector2i:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return INVALID_TILE
	var mouse_pos := get_viewport().get_mouse_position()
	return _ray_to_tile(cam.project_ray_origin(mouse_pos), cam.project_ray_normal(mouse_pos))


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
		"anim": _current_anim,
	}


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
