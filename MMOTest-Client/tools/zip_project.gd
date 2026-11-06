@tool
extends EditorScript
## Zips the whole project folder (the one that contains both the client and server projects),
## skipping the folders in EXCLUDED_FOLDERS.
##
## Run it with File > Run (Ctrl+Shift+X) while this script is open in the Script editor.
## The zip is written NEXT TO the project folder (in its parent), named <folder>_<date>_<time>.zip,
## and unzips into a single top-level folder.
##
## The root is assumed to be the parent of this Godot project's folder.

## Paths relative to the root folder, with forward slashes. Edit these if your folder names differ.
const EXCLUDED_FOLDERS := [
	".git",
	"MMOTest-Client/.godot",
	"MMOTest-Client/models",
	"MMOTest-Server/.godot",
	"MMOTest-Server/models",
	"MMOTest-Server/resources",
]

var _zip := ZIPPacker.new()
var _root := ""
var _root_name := ""
var _file_count := 0
var _byte_count := 0
var _matched: Dictionary = {}


func _run() -> void:
	var project_dir := ProjectSettings.globalize_path("res://").trim_suffix("/")
	_root = project_dir.get_base_dir()
	_root_name = _root.get_file()

	var stamp := Time.get_datetime_string_from_system().replace("T", "_").replace(":", "-")
	var zip_path := _root.get_base_dir().path_join("%s_%s.zip" % [_root_name, stamp])

	var err := _zip.open(zip_path)
	if err != OK:
		push_error("Zip: could not create %s (%s)" % [zip_path, error_string(err)])
		return

	print("Zip: zipping %s ..." % _root)
	_add_directory("")
	_zip.close()

	for excluded: String in EXCLUDED_FOLDERS:
		if not _matched.has(excluded):
			push_warning("Zip: excluded folder '%s' was not found under the root (check the name)" % excluded)

	print("Zip: done. %d files, %.1f MB -> %s" % [_file_count, _byte_count / 1048576.0, zip_path])


func _add_directory(rel_dir: String) -> void:
	var abs_dir := _root if rel_dir.is_empty() else _root.path_join(rel_dir)
	var dir := DirAccess.open(abs_dir)
	if dir == null:
		push_warning("Zip: cannot open %s" % abs_dir)
		return
	dir.include_hidden = true  # .gitignore, .gitattributes, .vscode, ...

	for folder in dir.get_directories():
		var rel := folder if rel_dir.is_empty() else rel_dir + "/" + folder
		if EXCLUDED_FOLDERS.has(rel):
			_matched[rel] = true
			print("Zip: skipping %s/" % rel)
			continue
		_add_directory(rel)

	for file_name in dir.get_files():
		_add_file(file_name if rel_dir.is_empty() else rel_dir + "/" + file_name)


func _add_file(rel_file: String) -> void:
	var bytes := FileAccess.get_file_as_bytes(_root.path_join(rel_file))
	if bytes.is_empty() and FileAccess.get_open_error() != OK:
		push_warning("Zip: could not read %s, skipped" % rel_file)
		return

	_zip.start_file(_root_name + "/" + rel_file)
	_zip.write_file(bytes)
	_zip.close_file()
	_file_count += 1
	_byte_count += bytes.size()
