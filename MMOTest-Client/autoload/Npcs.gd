extends Node
## Autoload "Npcs": client-side mirror of NPCs the server has told us about.
##
## Lives above the scene tree, so it catches every npc_joined / npc_left that
## arrives — including the initial EnterWorld snapshot, which fires before
## game.tscn exists. game.gd reads Npcs.all() in _ready to spawn whatever was
## cached during login. No timing race, no separate buffer in Network.
##
## Storage is the raw entry dict from the server, keyed by npc_id. When NPC
## movement lands, this gains a `_on_network_npc_moved` handler + `npc_moved`
## signal, mirroring Players.

signal changed
signal npc_added(npc_id: int)
signal npc_removed(npc_id: int)

var _npcs: Dictionary = {}   # npc_id (int) -> entry dict from server


func _ready() -> void:
	Network.npc_joined.connect(_on_network_npc_joined)
	Network.npc_left.connect(_on_network_npc_left)
	Network.state_changed.connect(_on_network_state_changed)


# ---------- Queries ----------

func all() -> Array:
	return _npcs.values()


func get_npc(npc_id: int) -> Dictionary:
	return _npcs.get(npc_id, {})


func has_npc(npc_id: int) -> bool:
	return _npcs.has(npc_id)


func count() -> int:
	return _npcs.size()


# ---------- Network handlers ----------

func _on_network_npc_joined(npcs: Array) -> void:
	var added_any := false
	for entry in npcs:
		if typeof(entry) != TYPE_DICTIONARY: continue
		var npc_id := int(entry.get("id", 0))
		if npc_id == 0: continue
		if _npcs.has(npc_id): continue
		_npcs[npc_id] = entry
		npc_added.emit(npc_id)
		added_any = true
	if added_any:
		changed.emit()


func _on_network_npc_left(ids: Array) -> void:
	var removed_any := false
	for id_v in ids:
		var npc_id := int(id_v)
		if _npcs.erase(npc_id):
			npc_removed.emit(npc_id)
			removed_any = true
	if removed_any:
		changed.emit()


func _on_network_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and not _npcs.is_empty():
		var ids := _npcs.keys()
		_npcs.clear()
		for id in ids:
			npc_removed.emit(int(id))
		changed.emit()
