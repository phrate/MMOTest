extends Node
## Autoload "Tooltips": builds right-click menus for entities present in
## Gfs.get_entries(). The presence of an entity type in entries means it has a
## tooltip_menu. All script content comes from the Gfs autoload's AST cache.

func has_tooltip_menu(entity_type: String) -> bool:
	return Gfs.has_entry(entity_type)


## Returns an Array of Dicts: [{id, script, display, args}].
## system_vars should contain SELF_ID and PLAYER_ID (and anything else the
## tooltip script may reference via ${NAME}).
func build_actions(entity_type: String, system_vars: Dictionary) -> Array:
	if not Gfs.has_entry(entity_type): return []
	var script_path: String = str(Gfs.get_entries()[entity_type])
	return Gfs.evaluate_tooltip(script_path, system_vars)
