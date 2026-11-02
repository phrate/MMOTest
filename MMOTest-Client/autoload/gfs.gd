extends Node
## Autoload "Gfs": the client's cache of GFS ASTs, shipped from the server.
## No lexer, no parser — only an AST evaluator.

signal sync_completed
signal sync_failed(reason: String)
signal scripts_updated

var is_loaded := false
var version := 0
var _entries: Dictionary = {}   # entity_type -> script_path
var _asts: Dictionary = {}      # script_path -> AST dictionary
var _sync_in_progress := false


func _ready() -> void:
	Network.gfs_info_received.connect(_on_gfs_info)
	Network.gfs_updated.connect(_on_gfs_updated)
	Network.state_changed.connect(_on_state_changed)


func request_sync() -> void:
	if _sync_in_progress: return
	if Network.state != Network.State.CONNECTED:
		sync_failed.emit("not_connected")
		return
	_sync_in_progress = true
	Network.request_gfs_info()


func get_ast(script_path: String) -> Dictionary:
	return _asts.get(script_path, {})


func get_entries() -> Dictionary:
	return _entries


func has_entry(entity_type: String) -> bool:
	return _entries.has(entity_type)


## Evaluate an AST against a system-vars dict. Returns the fully-evaluated
## value of the last `actions` assignment (or whatever was assigned last), plus
## the locals that were built up along the way.
func evaluate_tooltip(script_path: String, system_vars: Dictionary) -> Array:
	var ast: Dictionary = _asts.get(script_path, {})
	if ast.is_empty(): return []
	if ast.get("namespace", "") != "client": return []
	if ast.get("section", "") != "tooltip": return []

	var locals := {}
	var args := {}
	_execute_block(ast.get("statements", []), locals, args, system_vars)
	return locals.get("actions", [])


func _execute_block(stmts: Array, locals: Dictionary, args: Dictionary, system: Dictionary) -> void:
	for stmt in stmts:
		if typeof(stmt) != TYPE_DICTIONARY: continue
		var kind: String = stmt.get("kind", "")
		match kind:
			"assign":
				locals[str(stmt.get("name", ""))] = _evaluate(stmt.get("value", {}), locals, args, system)
			"if":
				var cond = _evaluate(stmt.get("condition", {}), locals, args, system)
				if _truthy(cond):
					_execute_block(stmt.get("then", []), locals, args, system)
				else:
					_execute_block(stmt.get("else", []), locals, args, system)
			"expr_stmt":
				_evaluate(stmt.get("expr", {}), locals, args, system)
			# prints are ignored on the client


func _truthy(v: Variant) -> bool:
	if v == null: return false
	var t := typeof(v)
	if t == TYPE_BOOL: return v
	if t == TYPE_INT or t == TYPE_FLOAT: return v != 0
	if t == TYPE_STRING: return not String(v).is_empty()
	if t == TYPE_ARRAY or t == TYPE_DICTIONARY: return not v.is_empty()
	return true


func _evaluate(node: Dictionary, locals: Dictionary, args: Dictionary, system: Dictionary) -> Variant:
	var kind: String = node.get("kind", "")
	match kind:
		"literal":
			return node.get("value")
		"null":
			return null
		"ref":
			var ref_kind: String = node.get("ref_kind", "")
			var name: String = node.get("name", "")
			match ref_kind:
				"local":  return locals.get(name)
				"arg":    return args.get(name)
				"system": return system.get(name)
			return null
		"array":
			var items := []
			for item in node.get("items", []):
				items.append(_evaluate(item, locals, args, system))
			return items
		"dict":
			var out := {}
			var entries: Dictionary = node.get("entries", {})
			for key in entries.keys():
				out[str(key)] = _evaluate(entries[key], locals, args, system)
			return out
		"action":
			var action_args := {}
			var raw_args: Dictionary = node.get("args", {})
			for key in raw_args.keys():
				action_args[str(key)] = _evaluate(raw_args[key], locals, args, system)
			return {
				"id": node.get("key", ""),
				"script": _evaluate(node.get("script_node", {}), locals, args, system),
				"display": _evaluate(node.get("display_node", {}), locals, args, system),
				"args": action_args,
			}
		"is_empty":
			var v := _evaluate(node.get("operand", {}), locals, args, system)
			var is_emp := _is_empty(v)
			return !is_emp if node.get("negated", false) else is_emp
		"call":
			# Client has no function registry yet; return null for now.
			push_warning("[Gfs] client-side call not supported: " + str(node.get("name", "")))
			return null
		_:
			return null


func _is_empty(v: Variant) -> bool:
	if v == null: return true
	var t := typeof(v)
	if t == TYPE_STRING or t == TYPE_STRING_NAME: return String(v).is_empty()
	if t == TYPE_ARRAY or t == TYPE_DICTIONARY or t == TYPE_PACKED_BYTE_ARRAY \
			or t == TYPE_PACKED_INT32_ARRAY or t == TYPE_PACKED_INT64_ARRAY \
			or t == TYPE_PACKED_FLOAT32_ARRAY or t == TYPE_PACKED_FLOAT64_ARRAY \
			or t == TYPE_PACKED_STRING_ARRAY or t == TYPE_PACKED_VECTOR2_ARRAY \
			or t == TYPE_PACKED_VECTOR3_ARRAY:
		return v.is_empty()
	return false


func _on_gfs_info(server_version: int, entries: Dictionary, scripts: Dictionary) -> void:
	if not _sync_in_progress: return
	_sync_in_progress = false
	version = server_version
	_entries = entries
	_asts = scripts
	is_loaded = true
	print("[Gfs] Loaded v%d, %d entries, %d scripts" % [version, entries.size(), scripts.size()])
	sync_completed.emit()


func _on_gfs_updated(server_version: int, scripts: Dictionary) -> void:
	if server_version <= version: return
	version = server_version
	for key in scripts.keys():
		var path: String = str(key)
		_asts[path] = scripts[key]
	print("[Gfs] Updated v%d, %d script(s) refreshed" % [version, scripts.size()])
	scripts_updated.emit()


func _on_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED and _sync_in_progress:
		_sync_in_progress = false
		sync_failed.emit("disconnected")
