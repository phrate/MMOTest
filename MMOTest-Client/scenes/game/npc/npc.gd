extends Node3D
## NPC avatar. Render scene + hitbox come from NpcRender's cache.

const CHAT_BUBBLE_SECONDS := 5.0
const MOVING_THRESHOLD_SQ := 0.0001

@export var move_speed := 2.0

var display: String = ""
var _npc_id: int = 0
var _target_pos: Vector3
var _target_yaw: float = 0.0
var _chat_hide_at_ms: int = 0
var _current_anim: String = ""
var _scripted_anim: String = ""
var _scripted_end_ms: int = 0
var _last_frame_pos := Vector3.ZERO
var _frame_vel := 0.0

var _render_slug: String = ""
var _render_scene_instance: Node3D = null
var _hitbox_is_fallback: bool = false

@onready var name_label: Label3D = get_node_or_null("NameLabel") as Label3D
@onready var chat_label: Label3D = _resolve_chat_label()
@onready var anim_player: AnimationPlayer = _ensure_anim_player()


func _ready() -> void:
	add_to_group("npc")
	_target_pos = global_position
	_target_yaw = rotation.y
	_last_frame_pos = global_position
	NpcRender.slug_ready.connect(_on_render_slug_ready)
	if _render_slug != "":
		_apply_render_scene()
	elif get_node_or_null("Hitbox") == null:
		_ensure_hitbox()
	_update_anim()


func set_npc_id(pid: int) -> void:
	_npc_id = pid

func get_npc_id() -> int:
	return _npc_id

func get_entity_type() -> String:
	return "npc"

func set_render_slug(slug: String) -> void:
	_render_slug = slug

func set_display(d: String) -> void:
	display = d
	if name_label == null: return
	name_label.text = d
	name_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	name_label.no_depth_test = true
	if name_label.position.y < 0.5:
		name_label.position.y = 2.2
	# Only rebuild the hitbox if we're on the fallback path (display feeds the
	# spec lookup). Editor-authored and scene-authored hitboxes stay untouched.
	if _hitbox_is_fallback or _render_slug == "":
		_ensure_hitbox()


func play_scripted_anim(anim: String, duration_ms: int) -> void:
	_scripted_anim = anim
	_scripted_end_ms = (Time.get_ticks_msec() + duration_ms) if duration_ms > 0 else int(0x7FFFFFFF)
	_current_anim = ""
	_update_anim()


# ---------- Render scene lifecycle ----------

func _on_render_slug_ready(slug: String) -> void:
	if slug != _render_slug: return
	# Bundle updated (or arrived) — reload the visual + hitbox.
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
	var scene := NpcRender.get_scene(_render_slug)

	if scene != null:
		if default_mesh != null: default_mesh.visible = false
		var inst := scene.instantiate()
		if inst is Node3D:
			inst.name = "RenderScene"
			add_child(inst)
			_render_scene_instance = inst
		else:
			inst.queue_free()
			if default_mesh != null: default_mesh.visible = true
	else:
		if default_mesh != null: default_mesh.visible = true

	var editor_hb := NpcRender.get_hitbox(_render_slug)
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
	var spec := Hitboxes.resolve("npc", display)
	Hitboxes.attach_to(self, spec)
	_hitbox_is_fallback = true


# ---------- Animation / chat / movement (unchanged from prior version) ----------

func teleport(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw
	if is_inside_tree():
		global_position = pos
		rotation.y = yaw
	_last_frame_pos = pos
	_frame_vel = 0.0
	_update_anim()


func set_target(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw


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
		var track := idle.add_track(Animation.TYPE_VALUE)
		idle.track_set_path(track, "Mesh:position")
		idle.track_insert_key(track, 0.0, Vector3.ZERO)
		lib.add_animation("idle", idle)
	if not ap.has_animation("walk"):
		var walk := Animation.new()
		walk.length = 0.4
		walk.loop_mode = Animation.LOOP_LINEAR
		var track := walk.add_track(Animation.TYPE_VALUE)
		walk.track_set_path(track, "Mesh:position")
		walk.track_insert_key(track, 0.0, Vector3.ZERO)
		walk.track_insert_key(track, 0.2, Vector3(0, 0.1, 0))
		walk.track_insert_key(track, 0.4, Vector3.ZERO)
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
	if anim_player.has_animation(desired):
		anim_player.play(desired)


func _derived_anim() -> String:
	return "walk" if _frame_vel > 0.5 else "idle"


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
