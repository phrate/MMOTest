extends Node
## Client mirror of the player's skills. The server sends `skills_data` on login (and again whenever
## skills change); we replace our copy and emit `changed`. Order is the server's display order.

signal changed

# [{id, level, max, current_xp, next_level, remaining_xp, max_level_reached}, ...]
var _skills: Array = []


func _ready() -> void:
	Network.skills_data_received.connect(_on_skills_data)
	Network.player_hp_updated.connect(_on_player_hp_updated)
	Network.player_respawned.connect(_on_player_respawned)
	Network.state_changed.connect(_on_state_changed)


func all() -> Array:
	return _skills


func get_skill(id: String) -> Dictionary:
	for skill: Dictionary in _skills:
		if skill["id"] == id:
			return skill
	return {}


## Sum of base levels.
func total_level() -> int:
	var total := 0
	for skill: Dictionary in _skills:
		total += int(skill["max"])
	return total


func _on_skills_data(skills: Array) -> void:
	_skills.clear()
	for entry in skills:
		if typeof(entry) != TYPE_DICTIONARY:
			continue
		_skills.append({
			"id": str(entry.get("id", "")),
			"level": int(entry.get("level", 1)),
			"max": int(entry.get("max", 1)),
			"current_xp": int(entry.get("current_xp", 0)),
			"next_level": int(entry.get("next_level", 0)),
			"remaining_xp": int(entry.get("remaining_xp", 0)),
			"max_level_reached": bool(entry.get("max_level_reached", false)),
		})
	changed.emit()


func _on_player_hp_updated(player_id: int, hitpoints: int, max_hitpoints: int) -> void:
	if player_id != Session.player_id:
		return
	_set_hitpoints(hitpoints, max_hitpoints)


func _on_player_respawned(_x: float, _y: float, _z: float, _yaw: float,
		hitpoints: int, max_hitpoints: int) -> void:
	_set_hitpoints(hitpoints, max_hitpoints)


func _set_hitpoints(hitpoints: int, max_hitpoints: int) -> void:
	for index in range(_skills.size()):
		if _skills[index]["id"] != "hitpoints":
			continue
		if _skills[index]["level"] == hitpoints and _skills[index]["max"] == max_hitpoints:
			return
		_skills[index]["level"] = hitpoints
		_skills[index]["max"] = max_hitpoints
		changed.emit()
		return


func _on_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and not _skills.is_empty():
		_skills.clear()
		changed.emit()
