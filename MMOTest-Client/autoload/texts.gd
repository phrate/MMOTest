extends Node
## Autoload "Texts": loads every JSON file under res://Data/Texts at startup and looks strings up by key.
## Must be the FIRST autoload so other scripts can use it in their _ready().
##
## Key = folder names + file name (minus "_texts") + nested JSON keys, joined with dots.
## See Data/Texts/README.md for the conventions.

const TEXTS_ROOT := "res://Data/Texts"
const FILE_SUFFIX := "_texts"

var _texts: Dictionary = {}  # flat: "account.login.error.invalid_credentials" -> "Invalid username or password."


func _ready() -> void:
	_load_directory(TEXTS_ROOT, PackedStringArray())
	#print("Texts: loaded %d strings" % _texts.size())


## Returns the text for `key` with {placeholders} filled from `params`.
## A missing key returns the key itself (so it is obvious on screen) and logs a warning.
func get_text(key: String, params: Dictionary = {}) -> String:
	if not _texts.has(key):
		push_warning("Texts: missing key '%s'" % key)
		return key
	var text: String = _texts[key]
	if params.is_empty():
		return text
	return text.format(_normalize(params))


func has_text(key: String) -> bool:
	return _texts.has(key)


# ---------- Loading ----------
func _load_directory(path: String, prefix: PackedStringArray) -> void:
	var dir := DirAccess.open(path)
	if dir == null:
		push_error("Texts: cannot open '%s'" % path)
		return
	for folder in dir.get_directories():
		_load_directory(path.path_join(folder), prefix + PackedStringArray([folder]))
	for file_name in dir.get_files():
		if file_name.get_extension() == "json":
			_load_file(path.path_join(file_name), prefix, file_name.get_basename())


func _load_file(path: String, prefix: PackedStringArray, base_name: String) -> void:
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	if typeof(parsed) != TYPE_DICTIONARY:
		push_error("Texts: '%s' is missing or not a valid JSON object" % path)
		return
	var name_part := base_name.trim_suffix(FILE_SUFFIX)
	_flatten(".".join(prefix + PackedStringArray([name_part])), parsed, path)


func _flatten(key_prefix: String, node: Dictionary, source: String) -> void:
	for child_key in node:
		var full_key := "%s.%s" % [key_prefix, child_key]
		var value: Variant = node[child_key]
		if value is Dictionary:
			_flatten(full_key, value, source)
		elif value is String:
			if _texts.has(full_key):
				push_warning("Texts: duplicate key '%s' (in %s)" % [full_key, source])
			_texts[full_key] = value
		else:
			push_warning("Texts: '%s' in %s must be a string or an object" % [full_key, source])


# JSON numbers always parse as floats (3 -> 3.0); show whole numbers without the ".0".
func _normalize(params: Dictionary) -> Dictionary:
	var result := {}
	for param_key in params:
		var value: Variant = params[param_key]
		if value is float and value == floorf(value):
			value = int(value)
		result[param_key] = value
	return result
