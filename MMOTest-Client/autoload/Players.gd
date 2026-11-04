extends Node
## Autoload "Players": the client's mirror of who else is online in the world.

signal changed
signal player_added(player_id: int)
signal player_removed(player_id: int)
signal player_moved(player_id: int)
signal player_hp_changed(player_id: int)
signal player_combat_state_changed(player_id: int)

var _players: Dictionary = {}
var _pending_positions: Dictionary = {}


func _ready() -> void:
	Network.world_state_received.connect(_on_world_state)
	Network.player_joined.connect(_on_player_joined)
	Network.player_left.connect(_on_player_left)
	Network.player_moved.connect(_on_network_player_moved)
	Network.state_changed.connect(_on_state_changed)
	Network.player_hp_updated.connect(_on_network_player_hp_updated)
	Network.player_combat_state.connect(_on_network_player_combat_state)


func all() -> Array:
	return _players.values()


func get_player(player_id: int) -> Dictionary:
	return _players.get(player_id, {})


func has_player(player_id: int) -> bool:
	return _players.has(player_id)


func count() -> int:
	return _players.size()


func _on_world_state(players: Array) -> void:
	_players.clear()
	for p in players:
		if typeof(p) == TYPE_DICTIONARY:
			_add(p)
	changed.emit()


func _on_player_joined(player_id: int, username: String, is_admin: bool,
		x: float, y: float, z: float, yaw: float, sid: int) -> void:
	var d := {
		"player_id": player_id,
		"username": username,
		"is_admin": is_admin,
		"x": x, "y": y, "z": z, "yaw": yaw,
		"sid": sid,
	}
	if _add(d):
		changed.emit()


func _on_player_left(player_id: int) -> void:
	_pending_positions.erase(player_id)
	if _players.erase(player_id):
		player_removed.emit(player_id)
		changed.emit()


func _on_network_player_moved(player_id: int, x: float, y: float, z: float, yaw: float) -> void:
	if player_id == Session.player_id:
		return
	var pos := Vector3(x, y, z)
	if not _players.has(player_id):
		_pending_positions[player_id] = {"position": pos, "yaw": yaw}
		return
	_players[player_id]["position"] = pos
	_players[player_id]["yaw"] = yaw
	player_moved.emit(player_id)


func _on_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and not _players.is_empty():
		var ids := _players.keys()
		_players.clear()
		_pending_positions.clear()
		for id in ids:
			player_removed.emit(id)
		changed.emit()


func _add(p: Dictionary) -> bool:
	var pid := int(p.get("player_id", 0))
	if pid == 0 or pid == Session.player_id:
		return false
	if _players.has(pid):
		return false

	var pos := Vector3(float(p.get("x", 0.0)), float(p.get("y", 0.0)), float(p.get("z", 0.0)))
	var yaw := float(p.get("yaw", 0.0))
	if _pending_positions.has(pid):
		pos = _pending_positions[pid]["position"]
		yaw = _pending_positions[pid]["yaw"]
		_pending_positions.erase(pid)

	_players[pid] = {
		"player_id": pid,
		"username": String(p.get("username", "")),
		"is_admin": bool(p.get("is_admin", false)),
		"position": pos,
		"yaw": yaw,
		"hitpoints": int(p.get("hitpoints", 10)),
		"max_hitpoints": int(p.get("max_hitpoints", 10)),
		"in_combat": false,
		"sid": int(p.get("sid", 0))
	}
	player_added.emit(pid)
	return true


func _on_network_player_hp_updated(player_id: int, hp: int, maxhp: int) -> void:
	if not _players.has(player_id): return
	_players[player_id]["hitpoints"] = hp
	_players[player_id]["max_hitpoints"] = maxhp
	player_hp_changed.emit(player_id)


func _on_network_player_combat_state(player_id: int, in_c: bool, _npc_id: int) -> void:
	if not _players.has(player_id): return
	_players[player_id]["in_combat"] = in_c
	player_combat_state_changed.emit(player_id)
