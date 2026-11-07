@tool
extends EditorScript
## Converts all .tscn files (mirrored paths) to res://tscn-converts/*.txt
## and res://project.godot to res://project.godot.txt.
##
## Run via File → Run (Ctrl+Shift+X) with this script open in the editor.

const TSCN_DEST := "res://tscn-converts"
const TSCN_SKIP_DIRS := ["tscn-converts"]
const PROJECT_GODOT_SRC := "res://project.godot"
const PROJECT_GODOT_DST := "res://project.godot.txt"

func _run() -> void:
	var stats := {"tscn": 0, "project_godot": false}

	if _convert_file(PROJECT_GODOT_SRC, PROJECT_GODOT_DST):
		stats.project_godot = true

	if DirAccess.make_dir_recursive_absolute(TSCN_DEST) != OK:
		push_error("Failed to create %s" % TSCN_DEST)
	else:
		_walk("res://", "", stats)

	print("=== Converted %d .tscn file(s) -> %s ===" % [stats.tscn, TSCN_DEST])
	print("=== project.godot -> %s: %s ===" % [PROJECT_GODOT_DST, "ok" if stats.project_godot else "FAILED"])

func _walk(abs_dir: String, rel_dir: String, stats: Dictionary) -> void:
	var dir := DirAccess.open(abs_dir)
	if dir == null:
		push_warning("Cannot open %s" % abs_dir)
		return
	dir.list_dir_begin()
	var name := dir.get_next()
	while name != "":
		var abs_path := abs_dir.path_join(name)
		var rel_path := rel_dir.path_join(name) if rel_dir != "" else name
		if dir.current_is_dir():
			if name not in TSCN_SKIP_DIRS and not name.begins_with("."):
				_walk(abs_path, rel_path, stats)
		elif name.ends_with(".tscn"):
			var dst := TSCN_DEST.path_join(rel_path + ".txt")
			if _convert_file(abs_path, dst):
				stats.tscn += 1
		name = dir.get_next()
	dir.list_dir_end()

func _convert_file(src: String, dst: String) -> bool:
	if DirAccess.make_dir_recursive_absolute(dst.get_base_dir()) != OK:
		push_error("Cannot create parent dir for %s" % dst)
		return false
	var src_f := FileAccess.open(src, FileAccess.READ)
	if src_f == null:
		push_error("Cannot read %s (error %d)" % [src, FileAccess.get_open_error()])
		return false
	var bytes := src_f.get_buffer(src_f.get_length())
	src_f.close()

	var dst_f := FileAccess.open(dst, FileAccess.WRITE)
	if dst_f == null:
		push_error("Cannot write %s (error %d)" % [dst, FileAccess.get_open_error()])
		return false
	dst_f.store_buffer(bytes)
	dst_f.close()

	print("  %s -> %s" % [src, dst])
	return true
