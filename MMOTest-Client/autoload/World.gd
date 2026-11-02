extends Node
## Autoload "World": loads the tile world from disk, exposes fast lookups, and syncs
## with the server when its world_version differs from the local copy.
##
## Format 2 introduces per-tile flags: {x, y, z, flags: {walkable: bool, ...}}.
## Every tile in the world is now stored explicitly. Unknown keys inside `flags`
## are preserved on load, so adding new flags later is a data change only.

signal loaded
signal load_failed(reason: String)
signal sync_started
signal sync_completed
signal sync_failed(reason: String)

const FORMAT_VERSION := 2
const LOCAL_PATHS := [
	"user://world.json",
	"res://data/world.json",
]
const USER_SAVE_PATH := "user://world.json"

var is_loaded := false
var format_version := 0
var world_version := 0
var chunk_size := 8
var default_z := 100
var z_min := 0
var z_max := 200

# Vector2i(x, y) -> {z: int, flags: Dictionary}
var _tiles: Dictionary = {}
# "cx,cy" -> Array of {x, y, z, flags}
var _chunks: Dictionary = {}

var _sync_in_progress := false


func _ready() -> void:
	Network.world_info_received.connect(_on_network_world_info)
	Network.world_data_received.connect(_on_network_world_data)
	Network.world_error.connect(_on_network_world_error)
	Network.state_changed.connect(_on_network_state_changed)
	load_local()


# ---------- Loading ----------

func load_local() -> bool:
	for path in LOCAL_PATHS:
		if not FileAccess.file_exists(path):
			continue
		var f := FileAccess.open(path, FileAccess.READ)
		if f == null:
			continue
		var text := f.get_as_text()
		f.close()

		var parsed: Variant = JSON.parse_string(text)
		if typeof(parsed) != TYPE_DICTIONARY:
			load_failed.emit(Texts.get_text("world.parse_failed"))
			return false
		if _apply(parsed):
			return true
	load_failed.emit(Texts.get_text("world.not_found"))
	return false


func _apply(data: Dictionary) -> bool:
	for required in ["format_version", "world_version", "chunks"]:
		if not data.has(required):
			load_failed.emit(Texts.get_text("world.missing_field"))
			return false

	format_version = int(data["format_version"])
	if format_version != FORMAT_VERSION:
		load_failed.emit(Texts.get_text("world.bad_format_version"))
		return false

	world_version = int(data["world_version"])
	chunk_size    = int(data.get("chunk_size", 8))
	default_z     = int(data.get("default_z", 100))
	z_min         = int(data.get("z_min", 0))
	z_max         = int(data.get("z_max", 200))

	if chunk_size <= 0:
		load_failed.emit(Texts.get_text("world.bad_chunk_size"))
		return false
	if z_min > z_max or default_z < z_min or default_z > z_max:
		load_failed.emit(Texts.get_text("world.bad_z_range"))
		return false

	var chunks: Variant = data["chunks"]
	if typeof(chunks) != TYPE_DICTIONARY:
		load_failed.emit(Texts.get_text("world.chunks_invalid"))
		return false

	_tiles.clear()
	_chunks.clear()

	var warnings := 0
	for chunk_key in chunks:
		var arr: Variant = chunks[chunk_key]
		if typeof(arr) != TYPE_ARRAY:
			warnings += 1
			continue

		var cx_cy := _parse_chunk_key(str(chunk_key))
		if cx_cy.x == 0x7FFFFFFF:
			warnings += 1
			continue

		var chunk_tiles: Array = []
		for entry in arr:
			var tile := _parse_tile(entry)
			if tile.is_empty():
				warnings += 1
				continue

			var wx: int = tile["x"]
			var wy: int = tile["y"]
			var wz: int = tile["z"]

			if world_to_chunk(wx, wy) != cx_cy:
				warnings += 1
				continue
			if wz < z_min or wz > z_max:
				warnings += 1
				continue

			var key := Vector2i(wx, wy)
			if _tiles.has(key):
				warnings += 1
			_tiles[key] = {"z": wz, "flags": tile["flags"]}
			chunk_tiles.append(tile)

		if not chunk_tiles.is_empty():
			_chunks[str(chunk_key)] = chunk_tiles

	if warnings > 0:
		push_warning("World: loaded with %d validation warnings" % warnings)

	is_loaded = true
	loaded.emit()
	return true


func _parse_chunk_key(s: String) -> Vector2i:
	var parts := s.split(",")
	if parts.size() != 2:
		return Vector2i(0x7FFFFFFF, 0x7FFFFFFF)
	if not parts[0].is_valid_int() or not parts[1].is_valid_int():
		return Vector2i(0x7FFFFFFF, 0x7FFFFFFF)
	return Vector2i(int(parts[0]), int(parts[1]))


func _parse_tile(entry: Variant) -> Dictionary:
	if typeof(entry) != TYPE_DICTIONARY:
		return {}
	var x := _to_int(entry.get("x"))
	var y := _to_int(entry.get("y"))
	var z := _to_int(entry.get("z"))
	if x == 0x7FFFFFFF or y == 0x7FFFFFFF or z == 0x7FFFFFFF:
		return {}

	var flags: Dictionary = {}
	var raw_flags: Variant = entry.get("flags", {})
	if typeof(raw_flags) == TYPE_DICTIONARY:
		flags = (raw_flags as Dictionary).duplicate(true)
	# Ensure walkable exists and is a bool. Missing → default true.
	if not flags.has("walkable"):
		flags["walkable"] = true
	else:
		flags["walkable"] = bool(flags["walkable"])

	return {"x": x, "y": y, "z": z, "flags": flags}


func _to_int(v: Variant) -> int:
	if typeof(v) == TYPE_INT:
		return v
	if typeof(v) == TYPE_FLOAT:
		var f: float = v
		if f == floorf(f):
			return int(f)
	return 0x7FFFFFFF


# ---------- Queries ----------

func get_z(x: int, y: int) -> int:
	var t: Variant = _tiles.get(Vector2i(x, y))
	if t == null:
		return default_z
	return int(t["z"])


func has_tile(x: int, y: int) -> bool:
	return _tiles.has(Vector2i(x, y))


## The full tile record {z, flags}, or {} if the tile is out of the world.
## Tiles not explicitly stored (outside any chunk) return {} — they read as
## default_z via get_z() and walkable via is_walkable().
func get_tile(x: int, y: int) -> Dictionary:
	return _tiles.get(Vector2i(x, y), {})


func is_walkable(x: int, y: int) -> bool:
	var t: Variant = _tiles.get(Vector2i(x, y))
	if t == null:
		return false          # unlisted = outside the world = not walkable
	return bool(t["flags"].get("walkable", true))


func has_chunk(cx: int, cy: int) -> bool:
	return _chunks.has("%d,%d" % [cx, cy])


func get_chunk(cx: int, cy: int) -> Array:
	return _chunks.get("%d,%d" % [cx, cy], [])


func get_chunks() -> Dictionary:
	return _chunks


func world_to_chunk(x: int, y: int) -> Vector2i:
	return Vector2i(
		floori(float(x) / float(chunk_size)),
		floori(float(y) / float(chunk_size)))


func chunk_origin(cx: int, cy: int) -> Vector2i:
	return Vector2i(cx * chunk_size, cy * chunk_size)


# ---------- Sync with server ----------

func request_sync() -> void:
	if _sync_in_progress:
		return
	if Network.state != Network.State.CONNECTED:
		sync_failed.emit(Texts.get_text("world.sync.not_connected"))
		return
	_sync_in_progress = true
	sync_started.emit()
	Network.request_world_info()


## Like request_sync(), but cancels any in-flight sync first. Used by the hotfix
## flow, which may fire while a previous sync is still resolving.
func force_sync() -> void:
	_sync_in_progress = false
	request_sync()


func _on_network_world_info(server_version: int, server_format: int) -> void:
	if not _sync_in_progress:
		return
	if server_format != FORMAT_VERSION:
		_sync_in_progress = false
		push_warning("World: server format %d != client format %d" % [server_format, FORMAT_VERSION])
		sync_failed.emit(Texts.get_text("world.format_mismatch"))
		return
	if is_loaded and world_version == server_version:
		_sync_in_progress = false
		sync_completed.emit()
		return
	Network.request_world_data()


func _on_network_world_data(data: Dictionary) -> void:
	if not _sync_in_progress:
		return
	_sync_in_progress = false

	var normalized := _ints_only(data)

	var f := FileAccess.open(USER_SAVE_PATH, FileAccess.WRITE)
	if f == null:
		sync_failed.emit(Texts.get_text("world.save_failed"))
		return
	f.store_string(JSON.stringify(normalized, "\t"))
	f.close()

	if not load_local():
		sync_failed.emit(Texts.get_text("world.reload_failed"))
		return

	sync_completed.emit()


func _on_network_world_error(reason: String) -> void:
	if not _sync_in_progress:
		return
	_sync_in_progress = false
	sync_failed.emit(reason)


func _on_network_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and _sync_in_progress:
		_sync_in_progress = false
		sync_failed.emit(Texts.get_text("world.sync.disconnected"))


## Recursively converts whole-number floats back to int. Godot's JSON parser
## returns every number as float; this keeps z values readable in the saved file.
func _ints_only(v: Variant) -> Variant:
	if typeof(v) == TYPE_FLOAT:
		var f: float = v
		if f == floorf(f):
			return int(f)
		return f
	if typeof(v) == TYPE_DICTIONARY:
		var d := {}
		for k in v:
			d[_ints_only(k)] = _ints_only(v[k])
		return d
	if typeof(v) == TYPE_ARRAY:
		var a := []
		for item in v:
			a.append(_ints_only(item))
		return a
	return v


func tile_count() -> int:
	return _tiles.size()


func chunk_count() -> int:
	return _chunks.size()
