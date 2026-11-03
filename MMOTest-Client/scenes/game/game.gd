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


func _ready() -> void:
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
	Network.chat_received.connect(_on_chat_received)
	Network.overhead_received.connect(_on_overhead_received)
	Network.anim_changed_received.connect(_on_anim_changed)


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
	node.teleport(
		Vector3(
			float(entry.get("x", 0.0)),
			float(entry.get("y", 0.0)),
			float(entry.get("z", 0.0))),
		0.0)
	var anim := str(entry.get("anim", ""))
	if anim != "":
		node.play_scripted_anim(anim, 0)
