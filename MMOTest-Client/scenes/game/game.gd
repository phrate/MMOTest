extends Node3D
## Gameplay scene. Spawns the local player and mirrors remote avatars from Players.
## Routes chat messages to the correct player's bubble (local or remote), applying
## the same chunk-distance filter the chat log uses.

const PLAYER_SCENE := preload("res://scenes/game/player/player.tscn")
const REMOTE_SCENE := preload("res://scenes/game/remote_player/remote_player.tscn")
const CHAT_CHUNK_RADIUS := 2

@export var players_root: Node3D

var _local_player: CharacterBody3D
var _remote_players: Dictionary = {}
var _remote_by_username: Dictionary = {}


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

	Players.player_added.connect(_on_player_added)
	Players.player_removed.connect(_on_player_removed)
	Players.player_moved.connect(_on_player_moved)
	Network.chat_received.connect(_on_chat_received)
	Network.overhead_received.connect(_on_overhead_received)


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
	if not is_inside_tree(): return
	if not _remote_players.has(player_id): return
	var data := Players.get_player(player_id)
	var node: Node3D = _remote_players[player_id]
	node.set_target(data["position"], data["yaw"])


func _spawn_remote(data: Dictionary) -> void:
	if not is_inside_tree(): return
	if players_root == null or not players_root.is_inside_tree(): return
	var player_id: int = data["player_id"]
	if _remote_players.has(player_id): return
	var node := REMOTE_SCENE.instantiate()
	node.set_player_id(player_id)                              # NEW — before add_child
	players_root.add_child(node)
	node.set_username(data["username"])
	node.teleport(data["position"], data["yaw"])
	_remote_players[player_id] = node
	_remote_by_username[data["username"]] = node


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
