extends Node3D
## NPC avatar. Delegates visuals to EntityVisuals child.

@export var move_speed := 2.0

var display: String = ""
var _npc_id := 0
var _target_pos: Vector3
var _target_yaw: float = 0.0
var _render_slug := ""
var _render_scene_instance: Node3D = null
var _hitbox_is_fallback := false

@onready var visuals: EntityVisuals = _make_visuals()


func _make_visuals() -> EntityVisuals:
	var v := EntityVisuals.new()
	v.name = "Visuals"
	add_child(v)
	return v


func _ready() -> void:
	add_to_group("npc")
	_target_pos = global_position
	_target_yaw = rotation.y
	NpcRender.slug_ready.connect(_on_render_slug_ready)
	_apply_render_scene()


# ---------- Public API (delegates) ----------

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
	visuals.set_name_text(d)
	# If the fallback hitbox was built with an empty display, rebuild with the
	# now-known one. Editor hitboxes are unaffected.
	if _hitbox_is_fallback:
		_ensure_hitbox()

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


# ---------- Render scene ----------

func _on_render_slug_ready(slug: String) -> void:
	if slug != _render_slug: return
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
	var ap: AnimationPlayer = null

	if scene != null:
		if default_mesh != null: default_mesh.visible = false
		var inst := scene.instantiate()
		if inst is Node3D:
			inst.name = "RenderScene"
			add_child(inst)
			_render_scene_instance = inst
			visuals.fit_render_scene_for_slug(inst, _render_slug)
			visuals.fit_name_label_above_node(inst)   # ← new line
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
	Hitboxes.attach_to(self, Hitboxes.resolve("npc", display))
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


# ---------- Movement ----------

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


func teleport(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw
	if is_inside_tree():
		global_position = pos
		rotation.y = yaw


func set_target(pos: Vector3, yaw: float) -> void:
	_target_pos = pos
	_target_yaw = yaw
