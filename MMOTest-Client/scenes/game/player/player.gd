extends CharacterBody3D
## Local player. Delegates visual state to EntityVisuals child.

signal walk_rejected(tile: Vector2i, reason: String)

const INVALID_TILE := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)
const RENDER_SLUG := "player"
const HOVER_LINGER_MS := 200

const ORB_SHADER := """
shader_type canvas_item;

uniform float fill : hint_range(0.0, 1.0) = 1.0;
uniform vec4 fill_color : source_color = vec4(0.15, 0.85, 0.15, 1.0);
uniform vec4 bg_color : source_color = vec4(0.30, 0.03, 0.03, 1.0);
uniform vec4 outline_color : source_color = vec4(0.05, 0.03, 0.02, 1.0);

void fragment() {
	vec2 c = UV - vec2(0.5);
	float d = length(c) * 2.0;
	if (d > 1.0) { discard; }
	float level = 1.0 - fill;
	vec3 col = (UV.y > level) ? fill_color.rgb : bg_color.rgb;
	if (d > 0.90) { col = outline_color.rgb; }
	COLOR = vec4(col, 1.0);
}
"""

@export var move_speed := 2.0
@export var camera: Camera3D

var _target_pos: Vector3
var _target_yaw: float = 0.0
var _waypoint_pos: Vector3 = Vector3(NAN, NAN, NAN)
var _waypoint_yaw: float = 0.0
var _pending_path_id := -1
var _last_request_id := -1
var _last_path_length := 0
var _last_request_goal := INVALID_TILE          # tile the player clicked
var _walking_to_message_destination := Vector3.INF  # last tile of the truncated path
var _walking_should_message := false
var _walking_message_shown := false

var _render_scene_instance: Node3D = null
var _hitbox_is_fallback := false

var _self_hp_orb: Control
var _self_hp_orb_material: ShaderMaterial
var _self_hp_label: Label

var _hover_layer: CanvasLayer
var _hover_panel: PanelContainer
var _hover_label: RichTextLabel
var _hover_target: Node3D = null
var _hover_last_hit_ms: int = 0

var _action_menu: ActionMenu
var _action_menu_layer: CanvasLayer
var _context_merged: Array = []

var _game_scene: Node = null

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
	Network.anim_changed_received.connect(_on_anim_changed)
	Pathfinder.path_found.connect(_on_path_found)
	Pathfinder.path_failed.connect(_on_path_failed)
	NpcRender.slug_ready.connect(_on_render_slug_ready)

	_apply_render_scene()
	_build_self_hp_orb()
	_build_hover_tooltip()
	_build_action_menu()
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
			visuals.fit_render_scene_for_slug(inst, RENDER_SLUG)
			visuals.fit_name_label_above_node(inst)
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
	if ap == null: return
	if not ap.get_animation_list().is_empty(): return
	var lib := ap.get_animation_library("")
	if lib == null:
		lib = AnimationLibrary.new()
		ap.add_animation_library("", lib)
	var idle := Animation.new()
	idle.length = 1.0
	idle.loop_mode = Animation.LOOP_LINEAR
	lib.add_animation("idle", idle)
	var walk := Animation.new()
	walk.length = 0.4
	walk.loop_mode = Animation.LOOP_LINEAR
	var track := walk.add_track(Animation.TYPE_VALUE)
	walk.track_set_path(track, "Mesh:position")
	walk.track_insert_key(track, 0.0, Vector3(0, 0.9, 0))
	walk.track_insert_key(track, 0.2, Vector3(0, 1.0, 0))
	walk.track_insert_key(track, 0.4, Vector3(0, 0.9, 0))
	lib.add_animation("walk", walk)


# ---------- Self HP orb ----------

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


# ---------- Hover tooltip ----------

func _build_hover_tooltip() -> void:
	_hover_layer = CanvasLayer.new()
	_hover_layer.name = "HoverTooltipLayer"
	_hover_layer.layer = 9
	add_child(_hover_layer)

	_hover_panel = PanelContainer.new()
	_hover_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_panel.visible = false
	_hover_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_hover_layer.add_child(_hover_panel)

	_hover_label = RichTextLabel.new()
	_hover_label.bbcode_enabled = true
	_hover_label.fit_content = true
	_hover_label.scroll_active = false
	_hover_label.autowrap_mode = TextServer.AUTOWRAP_OFF
	_hover_label.custom_minimum_size = Vector2(60, 0)
	_hover_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_label.add_theme_font_size_override("normal_font_size", UIStyle.FONT_SIZE)
	_hover_label.add_theme_constant_override("outline_size", UIStyle.OUTLINE_SIZE)
	_hover_label.add_theme_color_override("font_outline_color", UIStyle.OUTLINE_COLOR)
	_hover_panel.add_child(_hover_label)


func _update_hover_tooltip() -> void:
	if _hover_panel == null: return

	for m in get_tree().get_nodes_in_group("modal_blockers"):
		if m is CanvasItem and (m as CanvasItem).visible:
			_hover_target = null
			_hover_panel.visible = false
			return
	if get_viewport().gui_get_hovered_control() != null:
		_hover_target = null
		_hover_panel.visible = false
		return

	var candidate := _resolve_hover_target()
	var now := Time.get_ticks_msec()

	if candidate != null:
		_hover_target = candidate
		_hover_last_hit_ms = now
	elif _hover_target != null:
		if now - _hover_last_hit_ms > HOVER_LINGER_MS:
			_hover_target = null

	if _hover_target == null or not is_instance_valid(_hover_target) or not _hover_target.is_inside_tree():
		_hover_panel.visible = false
		return

	var etype := "player"
	if _hover_target.has_method("get_entity_type"):
		etype = _hover_target.get_entity_type()

	var actions: Array = []
	if etype == "ground_item":
		actions = Tooltips.build_actions("ground_item", {
			"SELF_ID": Session.player_id,
			"GROUND_ITEM_ID": _hover_target.get_ground_item_id(),
			"ITEM_SLUG": _hover_target.item_slug,
			"ITEM_DISPLAY_NAME": _hover_target.item_display,
			"ITEM_QUANTITY": _hover_target.item_quantity,
		})
	elif etype == "npc":
		actions = Tooltips.build_actions("npc", _npc_system_vars(_hover_target.get_npc_id()))
	else:
		_hover_panel.visible = false
		return

	if actions.is_empty():
		_hover_panel.visible = false
		return

	var primary: Dictionary = actions[0]
	_hover_label.text = UIStyle.format_primary_bbcode(
		str(primary.get("display", primary.get("id", "?"))))
	_hover_panel.reset_size()

	var mp := get_viewport().get_mouse_position()
	_hover_panel.position = Vector2i(mp + Vector2(16, 0))
	_hover_panel.visible = true


func _resolve_hover_target() -> Node3D:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return null

	var mp := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mp)
	var dir := cam.project_ray_normal(mp)

	var hit1 := _find_entity_under_ray_any(from, dir)
	if hit1.node == null: return null
	var entity1: Node3D = hit1.node

	var tile: Vector2i
	if entity1 == self:
		tile = Vector2i(roundi(global_position.x), roundi(global_position.z))
	else:
		tile = Vector2i(roundi(entity1.global_position.x), roundi(entity1.global_position.z))

	var entities: Array = []
	if _game() != null:
		entities = _game().get_entities_on_tile(tile)
	if entities.is_empty(): return null

	entities.sort_custom(_compare_entities_for_tooltip)
	var winner: Node3D = entities[0]
	if winner == null or not is_instance_valid(winner): return null

	var wtype := "player"
	if winner.has_method("get_entity_type"):
		wtype = winner.get_entity_type()
	if wtype == "player": return null
	return winner


func _body_owned_by(body: Node, entity: Node) -> bool:
	var n: Node = body
	while n != null:
		if n == entity: return true
		n = n.get_parent()
	return false


# ---------- Action menu ----------

func _build_action_menu() -> void:
	_action_menu_layer = CanvasLayer.new()
	_action_menu_layer.name = "ActionMenuLayer"
	_action_menu_layer.layer = 15
	add_child(_action_menu_layer)

	_action_menu = ActionMenu.new()
	_action_menu.name = "ActionMenu"
	_action_menu_layer.add_child(_action_menu)
	_action_menu.action_selected.connect(_on_action_menu_selected)


func _on_action_menu_selected(index: int) -> void:
	if index < 0 or index >= _context_merged.size(): return
	var item: Dictionary = _context_merged[index]
	var script_path: String = str(item.get("script", ""))
	if script_path.is_empty(): return
	var target_id: int = int(item.get("target_id", 0))
	Network.run_action(script_path, item.get("args", {}), target_id)


# ---------- Input ----------

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_LEFT:
			if _hover_panel: _hover_panel.visible = false
			_try_primary_click()
			get_viewport().set_input_as_handled()
		elif event.button_index == MOUSE_BUTTON_RIGHT:
			if _hover_panel: _hover_panel.visible = false
			_try_right_click()
			get_viewport().set_input_as_handled()


func _try_primary_click() -> void:
	var target := _resolve_hover_target()
	if target != null:
		var etype := "player"
		if target.has_method("get_entity_type"):
			etype = target.get_entity_type()
		if etype == "npc":
			var npc_id: int = target.get_npc_id()
			var actions: Array = Tooltips.build_actions("npc", _npc_system_vars(npc_id))
			if not actions.is_empty():
				var action: Dictionary = actions[0]
				var sp: String = str(action.get("script", ""))
				if not sp.is_empty():
					Network.run_action(sp, action.get("args", {}), npc_id)
					return
		elif etype == "ground_item":
			var gid: int = target.get_ground_item_id()
			if gid != 0:
				Network.send_pickup_ground_item(gid)
				return

	var cam := get_viewport().get_camera_3d()
	if cam == null: return
	var mp := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mp)
	var dir := cam.project_ray_normal(mp)
	var tile_hit := _ray_to_tile_with_t(from, dir)
	if tile_hit.tile == INVALID_TILE: return
	_request_path_to(tile_hit.tile)


func _try_right_click() -> void:
	var cam := get_viewport().get_camera_3d()
	if cam == null: return
	var mp := get_viewport().get_mouse_position()
	var from := cam.project_ray_origin(mp)
	var dir := cam.project_ray_normal(mp)

	var tile_hit := _ray_to_tile_with_t(from, dir)
	var entity_hit := _find_entity_under_ray_any(from, dir)
	if entity_hit.node == null: return
	if entity_hit.t > tile_hit.t: return

	var clicked: Node3D = entity_hit.node
	var tile: Vector2i
	if clicked == self:
		tile = Vector2i(roundi(global_position.x), roundi(global_position.z))
	else:
		tile = Vector2i(roundi(clicked.global_position.x), roundi(clicked.global_position.z))

	var entities: Array = []
	if _game() != null:
		entities = _game().get_entities_on_tile(tile)
	if entities.is_empty(): return

	entities.sort_custom(_compare_entities_for_tooltip)

	var merged: Array = []
	for node in entities:
		if not is_instance_valid(node): continue
		var etype := "player"
		if node.has_method("get_entity_type"):
			etype = node.get_entity_type()
		if not Tooltips.has_tooltip_menu(etype): continue

		var target_id := 0
		var system_vars := {}
		if etype == "npc":
			target_id = node.get_npc_id()
			system_vars = _npc_system_vars(target_id)
		elif etype == "ground_item":
			target_id = node.get_ground_item_id()
			system_vars = {
				"SELF_ID": Session.player_id,
				"GROUND_ITEM_ID": node.get_ground_item_id(),
				"ITEM_SLUG": node.item_slug,
				"ITEM_DISPLAY_NAME": node.item_display,
				"ITEM_QUANTITY": node.item_quantity,
			}
		else:
			target_id = node.get_player_id()
			var uname := ""
			if "username" in node:
				uname = str(node.username)
			system_vars = _player_system_vars(target_id, uname)

		var acts: Array = Tooltips.build_actions(etype, system_vars)
		for act in acts:
			merged.append({
				"display": str(act.get("display", "")),
				"script": str(act.get("script", "")),
				"args": act.get("args", {}),
				"target_id": target_id,
			})

	_show_merged_context_menu(merged, mp)


func _show_merged_context_menu(items: Array, mouse_pos: Vector2) -> void:
	if items.is_empty(): return
	_context_merged = items
	var displays: Array = []
	for item in items:
		displays.append(str(item.get("display", "?")))
	_action_menu.show_actions(displays, mouse_pos)


func _compare_entities_for_tooltip(a: Node, b: Node) -> bool:
	var ra := _entity_rank(a)
	var rb := _entity_rank(b)
	if ra != rb: return ra < rb

	var t := "player"
	if a.has_method("get_entity_type"): t = a.get_entity_type()

	if t == "ground_item":
		var da: String = str(a.item_display).to_lower()
		var db: String = str(b.item_display).to_lower()
		if da != db: return da < db
		return a.get_ground_item_id() < b.get_ground_item_id()

	return _entity_sub(a) < _entity_sub(b)


func _entity_rank(node: Node) -> int:
	if not is_instance_valid(node): return 999
	var t := "player"
	if node.has_method("get_entity_type"):
		t = node.get_entity_type()
	if t == "npc":
		if Session.combat_target_npc_id != 0 and node.get_npc_id() == Session.combat_target_npc_id:
			return 0
		return 2
	if t == "player": return 1
	if t == "ground_item": return 3
	return 999


func _entity_sub(node: Node) -> int:
	var t := "player"
	if node.has_method("get_entity_type"):
		t = node.get_entity_type()
	if t == "player":
		return int(Players.get_player(node.get_player_id()).get("sid", 0x7FFFFFFF))
	if t == "npc":
		return node.get_npc_id()
	if t == "ground_item":
		return node.get_ground_item_id()
	return 0x7FFFFFFF


# ---------- Path ----------

func _request_path_to(tile: Vector2i) -> void:
	_walking_should_message = false
	_walking_message_shown = false
	_walking_to_message_destination = Vector3.INF
	_last_request_goal = tile
	var cur := Vector2i(roundi(global_position.x), roundi(global_position.z))
	_pending_path_id = Pathfinder.request_path(cur, tile)
	_last_request_id = _pending_path_id
	_last_path_length = 0


func _on_path_found(request_id: int, path: Array) -> void:
	if request_id != _pending_path_id: return
	_pending_path_id = -1
	_last_path_length = path.size()
	if path.is_empty(): return
	var final_tile: Vector2i = path[-1]
	if final_tile != _last_request_goal:
		_walking_should_message = true
		_walking_to_message_destination = Vector3(
			final_tile.x, float(World.get_z(final_tile.x, final_tile.y)), final_tile.y)
	Network.send_walk_path(path)


func _on_path_failed(request_id: int, reason: String) -> void:
	if request_id != _pending_path_id: return
	_pending_path_id = -1
	_last_path_length = 0
	walk_rejected.emit(Vector2i.ZERO, reason)


# ---------- Movement / session ----------

func _process(delta: float) -> void:
	_update_hover_tooltip()

	var current_target := _target_pos
	var current_yaw := _target_yaw

	if not is_nan(_waypoint_pos.x):
		var to_waypoint := _waypoint_pos - global_position
		var to_target := _target_pos - global_position
		var dist_waypoint := to_waypoint.length()
		var dist_target := to_target.length()
		# Aim at the waypoint only while it is still between us and the target.
		# Once we've passed it (moved away from it on the way to the target),
		# the dot product goes negative and we stop aiming at it — otherwise
		# we'd oscillate: reach waypoint, move toward target, drift back past
		# waypoint, re-aim at it, and repeat every frame.
		if dist_waypoint > 0.01 and to_waypoint.dot(to_target) > 0.0:
			current_target = _waypoint_pos
			current_yaw = _waypoint_yaw

	var to_current := current_target - global_position
	var horiz_sq := to_current.x * to_current.x + to_current.z * to_current.z
	var speed := move_speed
	if horiz_sq > 0.0001:
		var horiz_dist := sqrt(horiz_sq)
		var dist_3d := to_current.length()
		speed = move_speed * (dist_3d / horiz_dist)
	var step := speed * delta
	if to_current.length_squared() <= step * step:
		global_position = current_target
	else:
		global_position = global_position.move_toward(current_target, step)
	rotation.y = current_yaw


func _on_self_position_updated(x: float, y: float, z: float, yaw: float,
		wx: float, wy: float, wz: float, wyaw: float) -> void:
	_target_pos = Vector3(x, y, z)
	_target_yaw = yaw
	if is_nan(wx):
		_waypoint_pos = Vector3(NAN, NAN, NAN)
	else:
		_waypoint_pos = Vector3(wx, wy, wz)
		_waypoint_yaw = wyaw


func _on_player_respawned(x: float, y: float, z: float, yaw: float, hp: int, max_hp: int) -> void:
	visuals.reset_hp_tracking()
	_pending_path_id = -1
	_last_path_length = 0
	_walking_should_message = false
	_walking_message_shown = false
	_walking_to_message_destination = Vector3.INF
	_waypoint_pos = Vector3(NAN, NAN, NAN)
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


func _on_anim_changed(player_id: int, anim: String, duration_ms: int) -> void:
	if player_id != Session.player_id: return
	play_scripted_anim(anim, duration_ms)


# ---------- Helpers ----------

func _game() -> Node:
	if _game_scene == null or not is_instance_valid(_game_scene):
		_game_scene = get_tree().get_first_node_in_group("game_scene")
	return _game_scene


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


# ---------- Ray helpers ----------

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
	if body.has_method("get_entity_type"):
		var b := body as Node3D
		if b == self: return {"node": null, "t": INF}
		return {"node": b, "t": from.distance_to(hit.position)}
	var entity: Node3D = body.get_parent()
	if entity == null or entity == self: return {"node": null, "t": INF}
	return {"node": entity, "t": from.distance_to(hit.position)}


func _find_entity_under_ray_any(from: Vector3, dir: Vector3) -> Dictionary:
	var space := get_world_3d().direct_space_state
	var q := PhysicsRayQueryParameters3D.create(
		from, from + dir * 300.0, Hitboxes.HITBOX_COLLISION_LAYER)
	q.collide_with_areas = false
	q.collide_with_bodies = true
	var hit := space.intersect_ray(q)
	if hit.is_empty(): return {"node": null, "t": INF}
	var body = hit.collider
	if not (body is StaticBody3D): return {"node": null, "t": INF}
	if body.has_method("get_entity_type"):
		return {"node": body as Node3D, "t": from.distance_to(hit.position)}
	var entity: Node3D = body.get_parent()
	if entity == null: return {"node": null, "t": INF}
	return {"node": entity, "t": from.distance_to(hit.position)}


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
