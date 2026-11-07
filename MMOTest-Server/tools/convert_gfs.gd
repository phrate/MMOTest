@tool
extends EditorScript

const DEST_DIR := "res://gfs-converts"
# Top-level directory names to skip entirely (so a repeat run doesn't re-copy
# its own output). Hidden dirs (.godot, .git, ...) are skipped automatically.
const SKIP_DIRS := ["gfs-converts"]

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(DEST_DIR) != OK:
		push_error("Failed to create %s" % DEST_DIR)
		return

	var stats := {"converted": 0, "collisions": 0}
	var seen := {}
	_walk("res://", "", seen, stats)

	print("=== Converted %d .gfs file(s) -> %s ===" % [stats.converted, DEST_DIR])
	if stats.collisions > 0:
		push_error("%d destination collision(s) — see warnings above." % stats.collisions)

func _walk(abs_dir: String, rel_dir: String, seen: Dictionary, stats: Dictionary) -> void:
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
			if name not in SKIP_DIRS and not name.begins_with("."):
				_walk(abs_path, rel_path, seen, stats)
		elif name.ends_with(".gfs"):
			var dst := DEST_DIR.path_join(rel_path + ".txt")
			if seen.has(dst):
				stats.collisions += 1
				push_warning("Collision at %s\n  first:  %s\n  second: %s" % [dst, seen[dst], abs_path])
			seen[dst] = abs_path
			if _copy(abs_path, dst):
				stats.converted += 1
		name = dir.get_next()
	dir.list_dir_end()

func _copy(src: String, dst: String) -> bool:
	# Ensure the mirrored parent directory exists before writing.
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
