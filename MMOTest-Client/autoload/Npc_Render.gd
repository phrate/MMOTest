extends Node
## Client-side cache + sync for NPC render scenes shipped by the server.
##
## Two trigger paths:
##   - initial login sync (world → gfs → npc_render)
##   - live bundle updates (server polls mtimes, broadcasts manifest diffs)
## Both flow through the same fetch loop.
##
## get_scene(slug) returns the real downloaded scene if cached, otherwise the
## client-resident fallback (res://models/y-bot-4.gltf). Any entity whose
## render scene fails to download or doesn't exist server-side still renders
## with the fallback — no capsules, no invisible entities.

signal sync_started
signal sync_completed
signal sync_failed(reason: String)
signal slug_ready(slug: String)

const CACHE_DIR := "user://npc_render_cache"
const FALLBACK_SCENE_PATH := "res://models/y-bot-4.gltf"

var _sync_in_progress := false
var _pending_slugs: Dictionary = {}
var _received_slugs: Dictionary = {}
var _scenes: Dictionary = {}
var _fallback_scene: PackedScene = null


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


## Returns the actual scene for this slug if cached, or the y-bot fallback.
## Never returns null unless the fallback itself is missing from the project.
func get_scene(slug: String) -> PackedScene:
	var real := _load_cached_scene(slug)
	if real != null: return real
	return _load_fallback_scene()


## True if the real (downloaded) scene for this slug is present. Ignores
## the fallback — callers use this to distinguish "still syncing" from
## "server doesn't have a scene for this slug."
func has_slug(slug: String) -> bool:
	return _load_cached_scene(slug) != null


## Reads the editor-authored hitbox for a slug, if one exists in the cache.
## Returns {} when absent or malformed.
func get_hitbox(slug: String) -> Dictionary:
	if slug.is_empty(): return {}
	var path := "%s/%s/hitbox.json" % [CACHE_DIR, slug]
	if not FileAccess.file_exists(path): return {}
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null: return {}
	var text := f.get_as_text()
	f.close()
	var parsed: Variant = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY: return {}
	return parsed as Dictionary


## Reads the editor-authored render config (mesh scale, etc.) for a slug.
## Returns {} when absent or malformed.
func get_render_config(slug: String) -> Dictionary:
	if slug.is_empty(): return {}
	var path := "%s/%s/render.json" % [CACHE_DIR, slug]
	if not FileAccess.file_exists(path): return {}
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null: return {}
	var text := f.get_as_text()
	f.close()
	var parsed: Variant = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY: return {}
	return parsed as Dictionary


# ---------- Internal scene loaders ----------

func _load_cached_scene(slug: String) -> PackedScene:
	if slug.is_empty(): return null
	if _scenes.has(slug): return _scenes[slug] as PackedScene
	var path := "%s/%s/%s.tscn" % [CACHE_DIR, slug, slug]
	if not ResourceLoader.exists(path): return null
	var res := load(path)
	if res is PackedScene:
		_scenes[slug] = res
		return res
	return null


func _load_fallback_scene() -> PackedScene:
	if _fallback_scene != null: return _fallback_scene
	if not ResourceLoader.exists(FALLBACK_SCENE_PATH):
		push_warning("[NpcRender] Fallback scene missing: " + FALLBACK_SCENE_PATH)
		return null
	var res := load(FALLBACK_SCENE_PATH)
	if res is PackedScene:
		_fallback_scene = res
		return res
	push_warning("[NpcRender] Fallback scene is not a PackedScene: " + FALLBACK_SCENE_PATH)
	return null


# ---------- Network handlers ----------

func _on_info(slugs: Dictionary) -> void:
	if not _sync_in_progress: return

	_prune_orphaned_caches(slugs)

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
	print("[NpcRender] Fetching %d slug(s): %s" % [to_fetch.size(), ", ".join(to_fetch)])
	Network.request_npc_render_fetch(to_fetch)


func _on_live_update(manifest: Dictionary) -> void:
	_prune_orphaned_caches(manifest)

	var to_fetch: Array = []
	for slug_v in manifest.keys():
		var slug := str(slug_v)
		var expected := str(manifest[slug_v])
		if _read_cached_hash(slug) != expected:
			_pending_slugs[slug] = expected
			to_fetch.append(slug)
	if to_fetch.is_empty(): return
	print("[NpcRender] Live update, fetching %d slug(s): %s" % [to_fetch.size(), ", ".join(to_fetch)])
	Network.request_npc_render_fetch(to_fetch)


## Delete cached scenes for slugs the server no longer hosts. Fires slug_ready
## on each removed slug so any entity using it re-applies its render scene
## (which will now hit the y-bot fallback).
func _prune_orphaned_caches(server_slugs: Dictionary) -> void:
	var dir := DirAccess.open(CACHE_DIR)
	if dir == null: return

	var removed: Array = []
	dir.list_dir_begin()
	var entry := dir.get_next()
	while entry != "":
		if dir.current_is_dir() and not entry.begins_with("."):
			if not server_slugs.has(entry):
				_remove_cache_dir(entry)
				_scenes.erase(entry)
				removed.append(entry)
		entry = dir.get_next()
	dir.list_dir_end()

	for slug in removed:
		print("[NpcRender] Pruned orphaned cache: %s" % slug)
		slug_ready.emit(slug)


func _remove_cache_dir(slug: String) -> void:
	var root := "%s/%s" % [CACHE_DIR, slug]
	var dir := DirAccess.open(root)
	if dir == null: return
	dir.list_dir_begin()
	var e := dir.get_next()
	while e != "":
		if dir.current_is_dir():
			var sub_dir := DirAccess.open(root + "/" + e)
			if sub_dir != null:
				sub_dir.list_dir_begin()
				var se := sub_dir.get_next()
				while se != "":
					if not sub_dir.current_is_dir():
						DirAccess.remove_absolute(root + "/" + e + "/" + se)
					se = sub_dir.get_next()
				sub_dir.list_dir_end()
			DirAccess.remove_absolute(root + "/" + e)
		else:
			DirAccess.remove_absolute(root + "/" + e)
		e = dir.get_next()
	dir.list_dir_end()
	DirAccess.remove_absolute(root)


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
