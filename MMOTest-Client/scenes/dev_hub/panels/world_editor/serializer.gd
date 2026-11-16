class_name WorldEditorSerializer
extends RefCounted
## Pure static helpers for building the world.json payload. No Godot nodes,
## no side effects — takes tiles as input, returns a serialized Dictionary
## ready to send to Network.send_dev_world_save().

const CHUNK_SIZE := 8


static func build_world_json(tiles: Dictionary, new_version: int) -> Dictionary:
	var chunk_map := {}
	for key in tiles:
		var x: int = key.x
		var y: int = key.y
		var cx := floori(float(x) / CHUNK_SIZE)
		var cy := floori(float(y) / CHUNK_SIZE)
		var chunk_key := "%d,%d" % [cx, cy]
		if not chunk_map.has(chunk_key):
			chunk_map[chunk_key] = []
		var d: Dictionary = tiles[key]
		var flags: Dictionary = d.get("flags", {})
		chunk_map[chunk_key].append({
			"x": x, "y": y, "z": int(d.get("z", 100)),
			"flags": {"walkable": bool(flags.get("walkable", true))}
		})

	for ck in chunk_map:
		chunk_map[ck].sort_custom(_tile_less)

	var sorted_keys := chunk_map.keys()
	sorted_keys.sort()
	var chunks := {}
	for k in sorted_keys:
		chunks[k] = chunk_map[k]

	return {
		"format_version": World.FORMAT_VERSION,
		"world_version": new_version,
		"chunk_size": World.chunk_size,
		"default_z": World.default_z,
		"z_min": World.z_min,
		"z_max": World.z_max,
		"chunks": chunks
	}


static func _tile_less(a: Dictionary, b: Dictionary) -> bool:
	if a["y"] != b["y"]: return a["y"] < b["y"]
	return a["x"] < b["x"]