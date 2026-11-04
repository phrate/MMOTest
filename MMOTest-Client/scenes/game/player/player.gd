extends CharacterBody3D
## Local player. Delegates all visual state to the EntityVisuals child.

signal walk_rejected(tile: Vector2i, reason: String)

const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)
const RENDER_SLUG := "player"
const ORB_SHADER := """
shader_type canvas_item;

uniform float fill : hint_range(0.0, 1.0) = 1.0;
uniform vec4 fill_color : source_color = vec4(0.15, 0.85, 0.15, 1.0);
uniform vec4 bg_color : source_color = vec4(0.30, 0.03, 0.03, 1.0);
uniform vec4 outline_color : source_color = vec4(0.05, 0.03, 0.02, 1.0);

void fragment() {
	vec2 c = UV - vec2(0.5);
	float d = length(c) * 2.0;
	if (d > 1.0) {
		discard;
	}
	float level = 1.0 - fill;
	vec3 col = (UV.y > level) ? fill_color.rgb : bg_color.rgb;
	if (d > 0.90) {
		col = outline_color.rgb;
	}
	COLOR = vec4(col, 1.0);
}
"""

@export var move_speed := 2.0
@export var camera: Camera3D

var _target_pos: Vector3
var _target_yaw: float = 0.0
var _pending_path_id := -1
var _last_request_id := -1
var _last_path_length := 0

var _render_scene_instance: Node3D = null
var _hitbox_is_fallback := false
var _self_hp_orb: Control
var _self_hp_orb_material: ShaderMaterial
var _self_hp_label: Label

var _context_menu: PopupMenu = null
var _context_target_id := 0
var _context_actions: Array = []

@onready var visuals: EntityVisuals = _make_visuals()


func _make_visuals() -> EntityVisuals:
	var v := EntityVisuals.new()
	v.name = "Visuals"
	add_child(v)
	return v


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
	Session.changed.connect(_on_session_changed)
	Network.self_position_updated.connect(_on_self_position_updated)
	Network.player_respawned.connect(_on_player_respawned)
	Pathfinder.path_found.connect(_on_path_found)
	Pathfinder.path_failed.connect(_on_path_failed)
	NpcRender.slug_ready.connect(_on_render_slug_ready)
	_apply_render_scene()
	_build_self_hp_orb()
	_on_session_changed()


# ---------- Public API (delegates) ----------

func set_username(u: String) -> void:
	visuals.set_name_text(u)

func show_chat_message(text: String) -> void:
	visuals.show_chat_message(text)

func update_hp(hp: int, max_hp: int) -> void:
	visuals.update_hp(hp, max_hp)

func bump_hp_bar() -> void:
	visuals.bump_hp_bar()

func show_damage(amount: int) -> void:
	visuals.show_damage(amount)

func play_scripted_anim(anim: String, duration_ms: int) -> void:
	visuals.play_scripted_anim(anim, duration_ms)

func set_in_combat(in_c: bool) -> void:
	visuals.set_in_combat(in_c)

func get_entity_type() -> String:
	return "player"


# ---------- Render scene ----------

func _on_render_slug_ready(slug: String) -> void:
	if slug != RENDER_SLUG: return
	_clear_render_scene()
	_apply_render_scene()


func _clear_render_scene() -> void:
	if _render_scene_instance != null and is_instance_valid(_render_scene_instance):
		_render_scene_instance.queue_free()
		_render_scene_instance = null
	var hb := get_node_or_null("Hitbox")
	if hb != null:
		remove_child(hb)
		hb.queue_free()


func _apply_render_scene() -> void:
	var default_mesh := get_node_or_null("Mesh") as Node3D
	var scene := NpcRender.get_scene(RENDER_SLUG)
	var ap: AnimationPlayer = null

	if scene != null:
		if default_mesh != null: default_mesh.visible = false
		var inst := scene.instantiate()
		if inst is Node3D:
			inst.name = "RenderScene"
			add_child(inst)
			_render_scene_instance = inst
			ap = _find_anim_player(inst)
			if ap == null:
				ap = AnimationPlayer.new()
				ap.name = "AnimationPlayer"
				inst.add_child(ap)
			_ensure_placeholder_anims(ap)
		else:
			inst.queue_free()
			if default_mesh != null: default_mesh.visible = true
	else:
		if default_mesh != null: default_mesh.visible = true
		ap = get_node_or_null("AnimationPlayer") as AnimationPlayer
		if ap == null:
			ap = AnimationPlayer.new()
			ap.name = "AnimationPlayer"
			add_child(ap)
		_ensure_placeholder_anims(ap)

	visuals.set_anim_player(ap)

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


func _find_anim_player(node: Node) -> AnimationPlayer:
	if node is AnimationPlayer: return node
	for c in node.get_children():
		var ap := _find_anim_player(c)
		if ap != null: return ap
	return null


func _ensure_placeholder_anims(ap: AnimationPlayer) -> void:
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


# ---------- Self HP bar ----------

func _build_self_hp_orb() -> void:
	var cl := CanvasLayer.new()
	cl.name = "SelfHpOrbLayer"
	cl.layer = 10
	add_child(cl)

	_self_hp_orb = Control.new()
	_self_hp_orb.name = "SelfHpOrb"
	_self_hp_orb.anchor_left = 1.0
	_self_hp_orb.anchor_right = 1.0
	_self_hp_orb.anchor_top = 0.0
	_self_hp_orb.anchor_bottom = 0.0
	_self_hp_orb.offset_left = -160.0
	_self_hp_orb.offset_right = -20.0
	_self_hp_orb.offset_top = 20.0
	_self_hp_orb.offset_bottom = 160.0
	_self_hp_orb.mouse_filter = Control.MOUSE_FILTER_IGNORE
	cl.add_child(_self_hp_orb)

	var rect := ColorRect.new()
	rect.name = "OrbFill"
	rect.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	rect.mouse_filter = Control.MOUSE_FILTER_IGNORE

	var shader := Shader.new()
	shader.code = ORB_SHADER
	var mat := ShaderMaterial.new()
	mat.shader = shader
	mat.set_shader_parameter("fill_color", HealthBarColors.SELF_FILL)
	mat.set_shader_parameter("bg_color", HealthBarColors.SELF_BG)
	mat.set_shader_parameter("outline_color", Color(0.05, 0.03, 0.02, 1.0))
	mat.set_shader_parameter("fill", 1.0)
	rect.material = mat
	_self_hp_orb.add_child(rect)
	_self_hp_orb_material = mat

	_self_hp_label = Label.new()
	_self_hp_label.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_self_hp_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_self_hp_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_self_hp_label.modulate = HealthBarColors.TARGET_TEXT
	_self_hp_label.add_theme_font_size_override("font_size", 18)
	_self_hp_label.add_theme_constant_override("outline_size", 5)
	_self_hp_label.add_theme_color_override("font_outline_color", Color(0, 0, 0, 1))
	_self_hp_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_self_hp_orb.add_child(_self_hp_label)


func _on_session_changed() -> void:
	var frac := clampf(float(Session.current_hp) / float(maxi(Session.max_hp, 1)), 0.0, 1.0)
	if _self_hp_orb_material:
		_self_hp_orb_material.set_shader_parameter("fill", frac)
	if _self_hp_label:
		_self_hp_label.text = "%d / %d" % [Session.current_hp, Session.max_hp]
	visuals.update_hp(Session.current_hp, Session.max_hp)


# ---------- Movement + input ----------

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_LEFT:
			_try_primary_click()
			get_viewport().set_input_as_handled()
		elif event.button_index == MOUSE_BUTTON_RIGHT:
			_try_right_click()
			get_viewport().set_input_as_handled()


func _try_primary_click() -> void:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return
	var mp := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mp)
	var dir := cam.project_ray_normal(mp)

	var tile_hit := _ray_to_tile_with_t(from, dir)
	var entity_hit := _find_entity_under_ray(from, dir)

	# If an entity is hit and it's in front of the tile, try its primary action.
	if entity_hit.node != null and entity_hit.t <= tile_hit.t:
		var node: Node3D = entity_hit.node
		var entity_type := "player"
		if node.has_method("get_entity_type"):
			entity_type = node.get_entity_type()

		# Only NPCs support primary-click actions. Players are click-through.
		if entity_type == "npc":
			var npc_id: int = node.get_npc_id()
			var actions: Array = Tooltips.build_actions("npc", _npc_system_vars(npc_id))
			if not actions.is_empty():
				var action: Dictionary = actions[0]
				var script_path: String = str(action.get("script", ""))
				if not script_path.is_empty():
					Network.run_action(script_path, action.get("args", {}), npc_id)
					return

	# Fall through: walk to the clicked tile.
	if tile_hit.tile == INVALID_TILE: return
	_request_path_to(tile_hit.tile)


func _try_right_click() -> void:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return
	var mp := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mp)
	var dir := cam.project_ray_normal(mp)

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
		system_vars = _npc_system_vars(target_id)
	else:
		target_id = node.get_player_id()
		var uname := ""
		if "username" in node:
			uname = str(node.username)
		system_vars = _player_system_vars(target_id, uname)

	var actions: Array = Tooltips.build_actions(entity_type, system_vars)
	_show_context_menu(target_id, actions, mp)


func _find_entity_under_ray(from: Vector3, dir: Vector3) -> Dictionary:
	var space := get_world_3d().direct_space_state
	var q := PhysicsRayQueryParameters3D.create(
		from, from + dir * 300.0, Hitboxes.HITBOX_COLLISION_LAYER)
	q.collide_with_areas = false
	q.collide_with_bodies = true
	var hit := space.intersect_ray(q)
	if hit.is_empty(): return {"node": null, "t": INF}
	var body = hit.collider
	if not (body is StaticBody3D): return {"node": null, "t": INF}
	var entity: Node3D = body.get_parent()
	if entity == null or entity == self: return {"node": null, "t": INF}
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
	var cur := Vector2i(roundi(global_position.x), roundi(global_position.z))
	_pending_path_id = Pathfinder.request_path(cur, tile)
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


func _on_player_respawned(x: float, y: float, z: float, yaw: float, hp: int, max_hp: int) -> void:
	visuals.reset_hp_tracking()
	# Cancel any in-flight pathfind so a late response can't re-trigger walking.
	_pending_path_id = -1
	_last_path_length = 0
	global_position = Vector3(x, y, z)
	rotation.y = yaw
	_target_pos = global_position
	_target_yaw = yaw
	Session.current_hp = hp
	Session.max_hp = max_hp
	Session.in_combat = false
	Session.combat_target_npc_id = 0
	Session.changed.emit()
	if camera != null and camera.has_method("snap_now"):
		camera.snap_now()


# ---------- Debug / ray ----------

func get_tile_under_mouse() -> Vector2i:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return INVALID_TILE
	var mp := get_viewport().get_mouse_position()
	return _ray_to_tile(cam.project_ray_origin(mp), cam.project_ray_normal(mp))


func get_debug_info() -> Dictionary:
	var ct := Vector2i(roundi(global_position.x), roundi(global_position.z))
	var tt := Vector2i(roundi(_target_pos.x), roundi(_target_pos.z))
	var state := "idle"
	if _pending_path_id != -1: state = "requesting"
	elif global_position.distance_squared_to(_target_pos) > 0.01: state = "walking"
	return {
		"current_tile": ct, "current_z": World.get_z(ct.x, ct.y),
		"target_tile": tt, "target_z": World.get_z(tt.x, tt.y),
		"state": state, "path_length": _last_path_length,
		"request_id": _last_request_id,
		"anim": visuals.get_current_anim(),
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


func _npc_system_vars(npc_id: int) -> Dictionary:
	var npc_data: Dictionary = Npcs.get_npc(npc_id)
	return {
		"SELF_ID": Session.player_id,
		"NPC_ID": npc_id,
		"NPC_DISPLAY_NAME": str(npc_data.get("display", "")),
		"NPC_COMBAT_FINAL_LEVEL": int(npc_data.get("final_level", 0)),
	}


func _player_system_vars(player_id: int, username: String) -> Dictionary:
	return {
		"SELF_ID": Session.player_id,
		"PLAYER_ID": player_id,
		"PLAYER_NAME": username,
	}
