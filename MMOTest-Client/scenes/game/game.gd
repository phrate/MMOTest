extends Node3D
## Gameplay scene. Spawns the local player and mirrors remote avatars from Players.
## Routes chat messages to the correct player's bubble (local or remote), applying
## the same chunk-distance filter the chat log uses.

const PLAYER_SCENE := preload("res://scenes/game/player/player.tscn")
const REMOTE_SCENE := preload("res://scenes/game/remote_player/remote_player.tscn")
const CHAT_CHUNK_RADIUS := 2
const NPC_SCENE := preload("res://scenes/game/npc/npc.tscn")

@export var players_root: Node3D

var _local_player: CharacterBody3D
var _remote_players: Dictionary = {}
var _remote_by_username: Dictionary = {}
var _npcs: Dictionary = {}   # npc_id -> Node3D
var _editor_overlay: Control
# Target HP bar
var _target_bar_panel: PanelContainer
var _target_bar_name: Label
var _target_bar_hp: Label
var _target_bar_fill: ColorRect
var _target_bar_npc_id: int = 0


func _ready() -> void:
	add_to_group("game_scene")
	process_priority = 100
	if not Session.is_logged_in():
		return

	if players_root == null:
		players_root = get_node_or_null("PlayersRoot") as Node3D
	if players_root == null:
		players_root = Node3D.new()
		players_root.name = "PlayersRoot"
		add_child(players_root)

	_local_player = PLAYER_SCENE.instantiate()
	players_root.add_child(_local_player)

	for data in Players.all():
		_spawn_remote(data)

	# Spawn NPCs the server sent during login (before this scene existed).
	for entry in Npcs.all():
		if typeof(entry) == TYPE_DICTIONARY:
			_instantiate_npc(entry)

	Players.player_added.connect(_on_player_added)
	Players.player_removed.connect(_on_player_removed)
	Players.player_moved.connect(_on_player_moved)
	Npcs.npc_added.connect(_on_npc_added)
	Npcs.npc_removed.connect(_on_npc_removed)
	Npcs.npc_moved.connect(_on_npc_moved)
	Network.chat_received.connect(_on_chat_received)
	Network.overhead_received.connect(_on_overhead_received)
	Network.anim_changed_received.connect(_on_anim_changed)
	Network.npc_anim_changed.connect(_on_npc_anim_changed)
	Npcs.npc_hp_changed.connect(_on_npc_hp_changed)
	Npcs.npc_replaced.connect(_on_npc_replaced)
	Players.player_hp_changed.connect(_on_player_hp_changed)
	Players.player_combat_state_changed.connect(_on_player_combat_state_changed)
	Network.player_combat_state.connect(_on_network_player_combat_state)
	Network.npc_editor_open.connect(_on_npc_editor_open)
	Network.damage_number.connect(_on_damage_number)

	_editor_overlay = preload("res://scenes/game/npc/npc_editor_overlay.gd").new()
	_editor_overlay.visible = false
	add_child(_editor_overlay)

	_build_target_hp_bar()


func _process(_delta: float) -> void:
	_update_visibility()


func _exit_tree() -> void:
	if Players.player_added.is_connected(_on_player_added):
		Players.player_added.disconnect(_on_player_added)
	if Players.player_removed.is_connected(_on_player_removed):
		Players.player_removed.disconnect(_on_player_removed)
	if Players.player_moved.is_connected(_on_player_moved):
		Players.player_moved.disconnect(_on_player_moved)
	if Network.chat_received.is_connected(_on_chat_received):
		Network.chat_received.disconnect(_on_chat_received)
	if Network.overhead_received.is_connected(_on_overhead_received):
		Network.overhead_received.disconnect(_on_overhead_received)
	if Network.anim_changed_received.is_connected(_on_anim_changed):
		Network.anim_changed_received.disconnect(_on_anim_changed)
	if Npcs.npc_added.is_connected(_on_npc_added):
		Npcs.npc_added.disconnect(_on_npc_added)
	if Npcs.npc_removed.is_connected(_on_npc_removed):
		Npcs.npc_removed.disconnect(_on_npc_removed)
	if Npcs.npc_moved.is_connected(_on_npc_moved):
		Npcs.npc_moved.disconnect(_on_npc_moved)
	if Network.npc_anim_changed.is_connected(_on_npc_anim_changed):
		Network.npc_anim_changed.disconnect(_on_npc_anim_changed)
	if Npcs.npc_hp_changed.is_connected(_on_npc_hp_changed):
		Npcs.npc_hp_changed.disconnect(_on_npc_hp_changed)
	if Npcs.npc_replaced.is_connected(_on_npc_replaced):
		Npcs.npc_replaced.disconnect(_on_npc_replaced)
	if Players.player_hp_changed.is_connected(_on_player_hp_changed):
		Players.player_hp_changed.disconnect(_on_player_hp_changed)
	if Players.player_combat_state_changed.is_connected(_on_player_combat_state_changed):
		Players.player_combat_state_changed.disconnect(_on_player_combat_state_changed)
	if Network.player_combat_state.is_connected(_on_network_player_combat_state):
		Network.player_combat_state.disconnect(_on_network_player_combat_state)
	if Network.npc_editor_open.is_connected(_on_npc_editor_open):
		Network.npc_editor_open.disconnect(_on_npc_editor_open)
	if Network.damage_number.is_connected(_on_damage_number):
		Network.damage_number.disconnect(_on_damage_number)


func _on_player_added(player_id: int) -> void:
	if not is_inside_tree(): return
	_spawn_remote(Players.get_player(player_id))


func _on_player_removed(player_id: int) -> void:
	if not is_inside_tree(): return
	if _remote_players.has(player_id):
		var node: Node3D = _remote_players[player_id]
		if node.username != "" and _remote_by_username.has(node.username):
			_remote_by_username.erase(node.username)
		node.queue_free()
		_remote_players.erase(player_id)


func _on_player_moved(player_id: int) -> void:
	if not _remote_players.has(player_id): return
	var data := Players.get_player(player_id)
	var node: Node3D = _remote_players[player_id]
	var new_pos: Vector3 = data["position"]
	# Snap instead of lerp when the gap is huge (e.g. the remote spawned at
	# Y=0 by mistake and the next tick reports Y=100). Normal movement deltas
	# are < 0.6 units per tick, so this threshold never fires for gameplay.
	if node.global_position.distance_to(new_pos) > 5.0:
		node.teleport(new_pos, data["yaw"])
	else:
		node.set_target(new_pos, data["yaw"])


func _spawn_remote(data: Dictionary) -> void:
	var player_id: int = data["player_id"]
	if _remote_players.has(player_id): return
	var node := REMOTE_SCENE.instantiate()
	node.set_player_id(player_id)
	players_root.add_child(node)
	node.set_username(data["username"])
	var pos: Vector3 = data["position"]
	# Snap Y to the tile's actual height. If the packet has a stale or
	# missing Y (Y=0 from an unpopulated fallback), the remote would
	# otherwise spawn below the floor and drift up during the first ticks.
	pos.y = float(World.get_z(roundi(pos.x), roundi(pos.z)))
	node.teleport(pos, data["yaw"])
	_remote_players[player_id] = node


func _on_chat_received(from_username: String, message: String, from_chunk: Vector2i) -> void:
	if not is_inside_tree(): return
	if _local_player == null: return

	var here := World.world_to_chunk(
		roundi(_local_player.global_position.x),
		roundi(_local_player.global_position.z))
	if absi(here.x - from_chunk.x) > CHAT_CHUNK_RADIUS \
			or absi(here.y - from_chunk.y) > CHAT_CHUNK_RADIUS:
		return

	if from_username == Session.username:
		_local_player.show_chat_message(message)
		return

	if _remote_by_username.has(from_username):
		_remote_by_username[from_username].show_chat_message(message)


func _on_overhead_received(from_username: String, text: String, from_chunk: Vector2i) -> void:
	if not is_inside_tree(): return
	if _local_player == null: return

	var here := World.world_to_chunk(
		roundi(_local_player.global_position.x),
		roundi(_local_player.global_position.z))
	if absi(here.x - from_chunk.x) > CHAT_CHUNK_RADIUS \
			or absi(here.y - from_chunk.y) > CHAT_CHUNK_RADIUS:
		return

	if from_username == Session.username:
		_local_player.show_chat_message(text)
		return

	if _remote_by_username.has(from_username):
		_remote_by_username[from_username].show_chat_message(text)


## Routes anim_changed packets to the correct player node. Self is handled by
## player.gd's own listener; this only routes to remotes.
func _on_anim_changed(player_id: int, anim: String, duration_ms: int) -> void:
	if not is_inside_tree(): return
	if player_id == Session.player_id: return
	if not _remote_players.has(player_id): return
	_remote_players[player_id].play_scripted_anim(anim, duration_ms)
	print("[Anim] player=", player_id, " anim=", anim, " dur=", duration_ms)


func _on_npc_added(npc_id: int) -> void:
	if not is_inside_tree(): return
	_instantiate_npc(Npcs.get_npc(npc_id))


func _on_npc_removed(npc_id: int) -> void:
	if not is_inside_tree(): return
	if _npcs.has(npc_id):
		_npcs[npc_id].queue_free()
		_npcs.erase(npc_id)


func _instantiate_npc(entry: Dictionary) -> void:
	var npc_id := int(entry.get("id", 0))
	if npc_id == 0: return
	var node: Node3D
	if _npcs.has(npc_id):
		node = _npcs[npc_id]
	else:
		node = NPC_SCENE.instantiate()
		node.set_render_slug(str(entry.get("render_slug", "")))   # ← BEFORE add_child
		players_root.add_child(node)
		node.set_npc_id(npc_id)
		_npcs[npc_id] = node
	node.set_display(str(entry.get("display", entry.get("name", ""))))
	node.update_hp(
		int(entry.get("hitpoints", 3)),
		int(entry.get("max_hitpoints", 3)))
	node.teleport(
		Vector3(
			float(entry.get("x", 0.0)),
			float(entry.get("y", 0.0)),
			float(entry.get("z", 0.0))),
		float(entry.get("yaw", 0.0)))
	var anim := str(entry.get("anim", ""))
	if anim != "":
		node.play_scripted_anim(anim, 0)


func _on_npc_moved(npc_id: int) -> void:
	if not _npcs.has(npc_id): return
	var data: Dictionary = Npcs.get_npc(npc_id)
	var node: Node3D = _npcs[npc_id]
	var new_pos := Vector3(
		float(data.get("x", 0.0)),
		float(data.get("y", 0.0)),
		float(data.get("z", 0.0)))
	var yaw := float(data.get("yaw", 0.0))
	# Snap on large gaps (teleport / chunk re-entry), interpolate otherwise.
	if node.global_position.distance_to(new_pos) > 5.0:
		node.teleport(new_pos, yaw)
	else:
		node.set_target(new_pos, yaw)


func _on_npc_anim_changed(npc_id: int, anim: String, duration_ms: int) -> void:
	if not _npcs.has(npc_id): return
	_npcs[npc_id].play_scripted_anim(anim, duration_ms)


func _on_npc_hp_changed(npc_id: int) -> void:
	if _npcs.has(npc_id):
		var d: Dictionary = Npcs.get_npc(npc_id)
		_npcs[npc_id].update_hp(
			int(d.get("hitpoints", 3)),
			int(d.get("max_hitpoints", 3)))
	if npc_id == _target_bar_npc_id:
		_refresh_target_bar()


func _on_npc_replaced(npc_id: int) -> void:
	if not _npcs.has(npc_id): return
	var d: Dictionary = Npcs.get_npc(npc_id)
	_npcs[npc_id].set_display(str(d.get("display", "")))
	_npcs[npc_id].update_hp(
		int(d.get("hitpoints", 3)),
		int(d.get("max_hitpoints", 3)))


func _on_player_hp_changed(player_id: int) -> void:
	if not _remote_players.has(player_id): return
	var d: Dictionary = Players.get_player(player_id)
	_remote_players[player_id].update_hp(
		int(d.get("hitpoints", 10)),
		int(d.get("max_hitpoints", 10)))


func _on_player_combat_state_changed(player_id: int) -> void:
	if not _remote_players.has(player_id): return
	var d: Dictionary = Players.get_player(player_id)
	_remote_players[player_id].set_in_combat(bool(d.get("in_combat", false)))


func _on_network_npc_combat_state(_npc_id: int, _in_combat: bool) -> void:
	pass  # handled via Npcs signal


func _on_network_player_combat_state(player_id: int, in_combat: bool, npc_id: int) -> void:
	if player_id != Session.player_id: return
	if in_combat:
		_target_bar_npc_id = npc_id
		_refresh_target_bar()
		_target_bar_panel.visible = true
	else:
		_target_bar_npc_id = 0
		_target_bar_panel.visible = false



func _on_npc_editor_open(npc_id: int) -> void:
	if _editor_overlay:
		_editor_overlay.open_for(npc_id)


# ---------- Target HP bar ----------
func _build_target_hp_bar() -> void:
	var cl := CanvasLayer.new()
	cl.name = "TargetBarLayer"
	cl.layer = 10
	add_child(cl)

	_target_bar_panel = PanelContainer.new()
	_target_bar_panel.anchor_left = 0.5
	_target_bar_panel.anchor_right = 0.5
	_target_bar_panel.anchor_top = 0.0
	_target_bar_panel.anchor_bottom = 0.0
	_target_bar_panel.offset_left = -180.0
	_target_bar_panel.offset_right = 180.0
	_target_bar_panel.offset_top = 20.0
	_target_bar_panel.offset_bottom = 90.0
	_target_bar_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_target_bar_panel.visible = false
	cl.add_child(_target_bar_panel)

	var vbox := VBoxContainer.new()
	vbox.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_target_bar_panel.add_child(vbox)

	_target_bar_name = Label.new()
	_target_bar_name.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_target_bar_name.modulate = HealthBarColors.TARGET_TEXT
	_target_bar_name.add_theme_font_size_override("font_size", 20)
	_target_bar_name.mouse_filter = Control.MOUSE_FILTER_IGNORE
	vbox.add_child(_target_bar_name)

	var bar_holder := Control.new()
	bar_holder.custom_minimum_size = Vector2(320, 24)
	bar_holder.mouse_filter = Control.MOUSE_FILTER_IGNORE
	vbox.add_child(bar_holder)

	var bg := ColorRect.new()
	bg.color = HealthBarColors.TARGET_BG
	bg.set_anchors_preset(Control.PRESET_FULL_RECT)
	bg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	bar_holder.add_child(bg)

	_target_bar_fill = ColorRect.new()
	_target_bar_fill.color = HealthBarColors.TARGET_FILL
	_target_bar_fill.anchor_left = 0.0
	_target_bar_fill.anchor_right = 1.0
	_target_bar_fill.anchor_top = 0.0
	_target_bar_fill.anchor_bottom = 1.0
	_target_bar_fill.offset_left = 2
	_target_bar_fill.offset_right = -2
	_target_bar_fill.offset_top = 2
	_target_bar_fill.offset_bottom = -2
	_target_bar_fill.mouse_filter = Control.MOUSE_FILTER_IGNORE
	bar_holder.add_child(_target_bar_fill)

	_target_bar_hp = Label.new()
	_target_bar_hp.set_anchors_preset(Control.PRESET_FULL_RECT)
	_target_bar_hp.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_target_bar_hp.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_target_bar_hp.modulate = HealthBarColors.TARGET_TEXT
	_target_bar_hp.add_theme_font_size_override("font_size", 16)
	_target_bar_hp.add_theme_constant_override("outline_size", 5)
	_target_bar_hp.add_theme_color_override("font_outline_color", Color(0, 0, 0, 1))
	_target_bar_hp.mouse_filter = Control.MOUSE_FILTER_IGNORE
	bar_holder.add_child(_target_bar_hp)


func _refresh_target_bar() -> void:
	if _target_bar_npc_id == 0: return
	var d: Dictionary = Npcs.get_npc(_target_bar_npc_id)
	if d.is_empty():
		_target_bar_panel.visible = false
		return
	var hp := int(d.get("hitpoints", 0))
	var maxhp := int(d.get("max_hitpoints", 1))
	_target_bar_name.text = str(d.get("display", "?"))
	_target_bar_hp.text = "%d / %d" % [hp, maxhp]
	var frac := clampf(float(hp) / float(maxi(maxhp, 1)), 0.0, 1.0)
	_target_bar_fill.anchor_right = frac
	_target_bar_fill.visible = frac > 0.001


func _on_damage_number(target_type: String, target_id: int, amount: int) -> void:
	match target_type:
		"npc":
			if _npcs.has(target_id):
				_npcs[target_id].show_damage(amount)
				_npcs[target_id].bump_hp_bar()
		"player":
			if target_id == Session.player_id:
				if _local_player != null:
					_local_player.show_damage(amount)
					_local_player.bump_hp_bar()
			elif _remote_players.has(target_id):
				_remote_players[target_id].show_damage(amount)
				_remote_players[target_id].bump_hp_bar()


func _update_visibility() -> void:
	# The local player's rounded tile — treated as an automatic priority winner.
	var local_tile := Vector2i(0x7FFFFFFF, 0x7FFFFFFF)
	if _local_player != null and is_instance_valid(_local_player):
		local_tile = Vector2i(
			roundi(_local_player.global_position.x),
			roundi(_local_player.global_position.z))

	var groups: Dictionary = {}   # Vector2i -> Array[Node3D]

	for id in _remote_players:
		var node: Node3D = _remote_players[id]
		if not is_instance_valid(node): continue
		var tile := Vector2i(roundi(node.global_position.x), roundi(node.global_position.z))
		if not groups.has(tile): groups[tile] = []
		groups[tile].append(node)

	for id in _npcs:
		var node: Node3D = _npcs[id]
		if not is_instance_valid(node): continue
		var tile := Vector2i(roundi(node.global_position.x), roundi(node.global_position.z))
		if not groups.has(tile): groups[tile] = []
		groups[tile].append(node)

	for tile in groups:
		var stack: Array = groups[tile]

		# Local player is on this tile — local player always wins rendering.
		# Hide every remote entity here (transient overlap during wander/pathing).
		if tile == local_tile:
			for node in stack:
				node.visible = false
			continue

		if stack.size() == 1:
			stack[0].visible = true
			continue

		var winner: Node3D = pick_priority_winner(tile, stack)
		for node in stack:
			node.visible = (node == winner)


## Returns all remote entities currently occupying `tile`, in raw (unsorted) order.
func get_entities_on_tile(tile: Vector2i) -> Array:
	var out: Array = []
	for id in _remote_players:
		var node: Node3D = _remote_players[id]
		if not is_instance_valid(node): continue
		if Vector2i(roundi(node.global_position.x), roundi(node.global_position.z)) == tile:
			out.append(node)
	for id in _npcs:
		var node: Node3D = _npcs[id]
		if not is_instance_valid(node): continue
		if Vector2i(roundi(node.global_position.x), roundi(node.global_position.z)) == tile:
			out.append(node)
	return out


## Priority winner for rendering + primary-click + hover:
##   1. An NPC the local player is currently in combat with.
##   2. A player (lower sid wins).
##   3. An NPC (lower id wins).
func pick_priority_winner(_tile: Vector2i, stack: Array) -> Node3D:
	# Priority 1: an NPC the local player is currently in combat with.
	for node in stack:
		if not is_instance_valid(node): continue
		if node.has_method("get_entity_type") and node.get_entity_type() == "npc":
			if Session.combat_target_npc_id != 0 and node.get_npc_id() == Session.combat_target_npc_id:
				return node

	# Priority 2: a player, lower sid wins.
	var best_player: Node3D = null
	var best_sid: int = 0x7FFFFFFF
	for node in stack:
		if not is_instance_valid(node): continue
		if node.has_method("get_entity_type") and node.get_entity_type() == "npc":
			continue
		var pid: int = node.get_player_id()
		var sid: int = int(Players.get_player(pid).get("sid", 0))
		if best_player == null or sid < best_sid:
			best_sid = sid
			best_player = node
	if best_player != null: return best_player

	# Priority 3: an NPC, lower id wins.
	var best_npc: Node3D = null
	var best_npc_id: int = 0x7FFFFFFF
	for node in stack:
		if not is_instance_valid(node): continue
		if not node.has_method("get_entity_type") or node.get_entity_type() != "npc":
			continue
		var nid: int = node.get_npc_id()
		if best_npc == null or nid < best_npc_id:
			best_npc_id = nid
			best_npc = node
	if best_npc != null: return best_npc

	return stack[0]
