extends Node
## Client-side cache of item icon PNGs shipped from the server.
## Mirrors the NpcRender fetch loop but for flat PNGs at item_render/icons/<name>.png.
## Cache dir: user://item_icon_cache/<name>.png + <name>.hash

signal sync_completed
signal icon_ready(name: String)

const CACHE_DIR := "user://item_icon_cache"

var _manifest: Dictionary = {}   # name -> hash
var _textures: Dictionary = {}   # name -> Texture2D
var _sync_in_progress := false
var _pending: Dictionary = {}    # name -> expected hash


func _ready() -> void:
	Network.item_icon_info_result.connect(_on_info)
	Network.item_icon_data_received.connect(_on_data)


func request_sync() -> void:
	if _sync_in_progress: return
	if Network.state != Network.State.CONNECTED: return
	_sync_in_progress = true
	_pending.clear()
	Network.request_item_icon_info()


func get_texture(name: String) -> Texture2D:
	if name.is_empty(): return null
	if _textures.has(name): return _textures[name]
	var path := "%s/%s.png" % [CACHE_DIR, name]
	if not FileAccess.file_exists(path): return null
	var img := Image.new()
	if img.load(path) != OK: return null
	var tex := ImageTexture.create_from_image(img)
	_textures[name] = tex
	return tex


func has_icon(name: String) -> bool:
	return get_texture(name) != null


func _on_info(ok: bool, manifest: Dictionary) -> void:
	if not ok: return
	_manifest = manifest
	var to_fetch: Array = []
	for name_v in manifest.keys():
		var name := str(name_v)
		var expected := str(manifest[name_v])
		if _read_cached_hash(name) != expected:
			_pending[name] = expected
			to_fetch.append(name)
	if to_fetch.is_empty():
		_finish_sync()
		return
	print("[ItemIcons] Fetching %d icon(s)" % to_fetch.size())
	Network.request_item_icon_fetch(to_fetch)


func _on_data(name: String, hash: String, data_base64: String) -> void:
	if not _pending.has(name): return
	if str(_pending[name]) != hash:
		push_warning("[ItemIcons] Hash mismatch for '%s'" % name)
		return

	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(CACHE_DIR))
	var raw := Marshalls.base64_to_raw(data_base64)
	var path := "%s/%s.png" % [CACHE_DIR, name]
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f == null: return
	f.store_buffer(raw)
	f.close()

	var hf := FileAccess.open("%s/%s.hash" % [CACHE_DIR, name], FileAccess.WRITE)
	if hf != null: hf.store_string(hash); hf.close()

	_textures.erase(name)
	_pending.erase(name)
	icon_ready.emit(name)

	if _sync_in_progress and _pending.is_empty():
		_finish_sync()


func _finish_sync() -> void:
	_sync_in_progress = false
	sync_completed.emit()


func _read_cached_hash(name: String) -> String:
	var path := "%s/%s.hash" % [CACHE_DIR, name]
	if not FileAccess.file_exists(path): return ""
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null: return ""
	var s := f.get_as_text().strip_edges()
	f.close()
	return s
