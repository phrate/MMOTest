extends Node
## Client-side cache + sync for NPC render scenes shipped by the server.
##
## Two trigger paths:
##   - initial login sync (world → gfs → npc_render)
##   - live bundle updates (server polls mtimes, broadcasts manifest diffs)
## Both flow through the same fetch loop.

signal sync_started
signal sync_completed
signal sync_failed(reason: String)
signal slug_ready(slug: String)

const CACHE_DIR := "user://npc_render_cache"

var _sync_in_progress := false
var _pending_slugs: Dictionary = {}
var _received_slugs: Dictionary = {}
var _scenes: Dictionary = {}


func _ready() -> void:
	Network.npc_render_info_received.connect(_on_info)
	Network.npc_render_data_received.connect(_on_data)
	Network.npc_render_updated_received.connect(_on_live_update)


func request_sync() -> void:
	if _sync_in_progress: return
	if Network.state != Network.State.CONNECTED:
		sync_failed.emit("not_connected")
		return
	_sync_in_progress = true
	_pending_slugs.clear()
	_received_slugs.clear()
	sync_started.emit()
	Network.request_npc_render_info()


func get_scene(slug: String) -> PackedScene:
	if slug.is_empty(): return null
	if _scenes.has(slug): return _scenes[slug] as PackedScene
	var path := "%s/%s/%s.tscn" % [CACHE_DIR, slug, slug]
	if not ResourceLoader.exists(path): return null
	var res := load(path)
	if res is PackedScene:
		_scenes[slug] = res
		return res
	return null


## Reads the editor-authored hitbox for a slug, if one exists in the cache.
## Returns {} when absent or malformed.
func get_hitbox(slug: String) -> Dictionary:
	if slug.is_empty(): return {}
	var path := "%s/%s/hitbox.json" % [CACHE_DIR, slug]
	var exists := FileAccess.file_exists(path)
	if not exists: return {}
	if not FileAccess.file_exists(path): return {}
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null: return {}
	var text := f.get_as_text()
	f.close()
	var parsed: Variant = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY: return {}
	return parsed as Dictionary


func has_slug(slug: String) -> bool:
	return get_scene(slug) != null


# ---------- Network handlers ----------

func _on_info(slugs: Dictionary) -> void:
	if not _sync_in_progress: return
	var to_fetch: Array = []
	for slug_v in slugs.keys():
		var slug := str(slug_v)
		var expected := str(slugs[slug_v])
		if _read_cached_hash(slug) != expected:
			_pending_slugs[slug] = expected
			to_fetch.append(slug)
	if to_fetch.is_empty():
		_finish_sync()
		return
	Network.request_npc_render_fetch(to_fetch)


func _on_live_update(manifest: Dictionary) -> void:
	# Same diff logic, but no sync_completed fire — this is a live refresh.
	var to_fetch: Array = []
	for slug_v in manifest.keys():
		var slug := str(slug_v)
		var expected := str(manifest[slug_v])
		if _read_cached_hash(slug) != expected:
			_pending_slugs[slug] = expected
			to_fetch.append(slug)
	if to_fetch.is_empty(): return
	Network.request_npc_render_fetch(to_fetch)


func _on_data(slug: String, file_hash: String, files: Dictionary) -> void:
	if not _pending_slugs.has(slug): return
	if str(_pending_slugs[slug]) != file_hash:
		push_warning("[NpcRender] Hash mismatch for '%s'" % slug)
		return

	var dir := "%s/%s" % [CACHE_DIR, slug]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))

	for rel_v in files.keys():
		var rel := str(rel_v)
		var path := dir + "/" + rel
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path.get_base_dir()))
		var raw: PackedByteArray = Marshalls.base64_to_raw(str(files[rel_v]))
		var ext := rel.get_extension().to_lower()
		var f := FileAccess.open(path, FileAccess.WRITE)
		if f == null:
			push_warning("[NpcRender] Cannot write %s" % path)
			continue
		if ext in ["tscn", "tres", "gd", "json"]:
			var text := raw.get_string_from_utf8()
			text = text.replace("res://npc_render/" + slug + "/",
								"user://npc_render_cache/" + slug + "/")
			f.store_string(text)
		else:
			f.store_buffer(raw)
		f.close()

	_write_cached_hash(slug, file_hash)
	_received_slugs[slug] = true
	_pending_slugs.erase(slug)
	_scenes.erase(slug)
	slug_ready.emit(slug)

	if _sync_in_progress and _pending_slugs.is_empty():
		_finish_sync()


func _finish_sync() -> void:
	_sync_in_progress = false
	print("[NpcRender] Sync complete, %d slug(s) cached" % _received_slugs.size())
	sync_completed.emit()


# ---------- Cache hash tracking ----------

func _read_cached_hash(slug: String) -> String:
	var path := "%s/%s/.hash" % [CACHE_DIR, slug]
	if not FileAccess.file_exists(path): return ""
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null: return ""
	var s := f.get_as_text().strip_edges()
	f.close()
	return s


func _write_cached_hash(slug: String, h: String) -> void:
	var f := FileAccess.open("%s/%s/.hash" % [CACHE_DIR, slug], FileAccess.WRITE)
	if f == null: return
	f.store_string(h)
	f.close()
