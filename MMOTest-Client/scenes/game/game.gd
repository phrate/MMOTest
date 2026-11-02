extends Node3D
## Gameplay scene. Spawns the local player and mirrors remote avatars from Players.
##
## Signals from the Players autoload are guarded with is_inside_tree() because
## during a hotfix scene reload, the old scene keeps receiving events for a few
## frames after it's been detached from the tree — its spawn target is no longer
## in the tree, so add_child() on it silently produces an orphan node.

const PLAYER_SCENE := preload("res://scenes/game/player/player.tscn")
const REMOTE_SCENE := preload("res://scenes/game/remote_player/remote_player.tscn")

@export var players_root: Node3D

var _local_player: CharacterBody3D
var _remote_players: Dictionary = {}


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


func _exit_tree() -> void:
	# Stop receiving Players events the moment we leave the tree. Without this,
	# the disconnect only happens at actual free time (end of frame), leaving a
	# window where _spawn_remote can run against a detached scene.
	if Players.player_added.is_connected(_on_player_added):
		Players.player_added.disconnect(_on_player_added)
	if Players.player_removed.is_connected(_on_player_removed):
		Players.player_removed.disconnect(_on_player_removed)
	if Players.player_moved.is_connected(_on_player_moved):
		Players.player_moved.disconnect(_on_player_moved)


func _on_player_added(player_id: int) -> void:
	if not is_inside_tree(): return
	_spawn_remote(Players.get_player(player_id))


func _on_player_removed(player_id: int) -> void:
	if not is_inside_tree(): return
	if _remote_players.has(player_id):
		_remote_players[player_id].queue_free()
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
	players_root.add_child(node)
	node.set_username(data["username"])
	node.teleport(data["position"], data["yaw"])
	_remote_players[player_id] = node
