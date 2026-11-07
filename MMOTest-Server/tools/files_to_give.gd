@tool
extends EditorScript

const OUTPUT_PATH := "res://files-to-give.txt"
const PREFIX := "Server"
const SKIP_DIR_SEGMENTS := [
	"models", "resources", ".vscode", ".godot",
	".git",
]
const SKIP_EXTENSIONS := [
	".guid", ".uid", ".csproj",
	".gfs",
	".tres", ".res",
	".import", ".fbx", ".gltf", ".glb", ".png",
]

func _run() -> void:
	var paths: Array[String] = []
	_walk("res://", paths)
	paths.sort()

	var lines: Array[String] = []
	for i in paths.size():
		var rel := paths[i].trim_prefix("res://")
		lines.append("[%s #%d] /%s" % [PREFIX, i + 1, rel])

	var f := FileAccess.open(OUTPUT_PATH, FileAccess.WRITE)
	if f == null:
		push_error("Cannot write %s (error %d)" % [OUTPUT_PATH, FileAccess.get_open_error()])
		return
	f.store_string("\n".join(lines) + "\n")
	f.close()

	print("Wrote %d file(s) to %s" % [lines.size(), OUTPUT_PATH])
	for i in min(5, lines.size()):
		print("  ", lines[i])
	if lines.size() > 5:
		print("  ...")

func _walk(abs_dir: String, out: Array[String]) -> void:
	var dir := DirAccess.open(abs_dir)
	if dir == null:
		push_warning("Cannot open %s" % abs_dir)
		return
	dir.list_dir_begin()
	var name := dir.get_next()
	while name != "":
		var full := abs_dir.path_join(name)
		if dir.current_is_dir():
			if not _segment_excluded(name):
				_walk(full, out)
		elif full != OUTPUT_PATH and not _extension_excluded(name):
			out.append(full)
		name = dir.get_next()
	dir.list_dir_end()

func _segment_excluded(segment: String) -> bool:
	var lower := segment.to_lower()
	for s in SKIP_DIR_SEGMENTS:
		if lower == s:
			return true
	return false

func _extension_excluded(filename: String) -> bool:
	var ext := "." + filename.get_extension().to_lower()
	return ext in SKIP_EXTENSIONS
