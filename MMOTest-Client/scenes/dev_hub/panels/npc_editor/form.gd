class_name NpcEditorForm
extends VBoxContainer
## Spec-driven NPC type form. Used identically for Edit (readonly slug,
## references section) and Create (editable slug, no references).
## Call set_data(src, is_edit, options) to populate; get_payload() to collect.
signal render_slug_changed(slug: String)

const VIEW_SPEC := [
	{"kind": "row", "fields": [
		{"kind": "line_edit", "label": "label_slug_id", "save": "slug_id", "value_key": "slug_id"},
		{"kind": "line_edit", "label": "label_display", "save": "display", "value_key": "display"},
	]},
	{"kind": "row", "fields": [
		{"kind": "dropdown", "label": "label_render", "save": "render_slug", "value_key": "render_slug", "options_key": "render_slugs"},
		{"kind": "dropdown", "label": "label_dialogue_script", "save": "dialogue_script", "value_key": "dialogue_script", "options_key": "dialogue_scripts", "prepend": ["_none"], "label_to_value": {"_none": ""}},
	]},
	{"kind": "separator"},
	{"kind": "row", "fields": [
		{"kind": "dropdown", "label": "label_loot_table", "save": "loot_table_name", "value_key": "loot_table_name", "options_key": "loot_lists"},
		{"kind": "dropdown", "label": "label_credits_table", "save": "credits_table_name", "value_key": "credits_table_name", "options_key": "credits_loot_lists"},
	]},
	{"kind": "row", "fields": [
		{"kind": "dropdown", "label": "label_ai_package", "save": "ai_package_state", "value_key": "ai_package_state", "options_static": ["idle", "wander"]},
	]},
	{"kind": "row", "fields": [
		{"kind": "textarea", "label": "label_spawn_flags", "save": "spawn_flags", "value_key": "spawn_flags"},
	]},
	{"kind": "separator"},
	{"kind": "section", "key": "section_features"},
	{"kind": "row", "fields": [
		{"kind": "toggle", "label": "label_is_banker", "save": "is_banker", "value_key": "is_banker"},
		{"kind": "toggle", "label": "label_attackable", "save": "attackable", "value_key": "attackable"},
		{"kind": "toggle", "label": "label_aggressive", "save": "aggressive", "value_key": "aggressive"},
	]},
	{"kind": "separator"},
	{"kind": "section", "key": "section_combat"},
	{"kind": "combat_grid"},
]

var _src: Dictionary = {}
var _is_edit: bool = false
var _options: Dictionary = {}
var _fields: Dictionary = {}
var _combat_grid: NpcEditorCombatGrid = null


func _init() -> void:
	add_theme_constant_override("separation", 4)


func set_data(src: Dictionary, is_edit: bool, options: Dictionary) -> void:
	_src = src.duplicate()
	_is_edit = is_edit
	_options = options
	_rebuild()


func _rebuild() -> void:
	for c in get_children():
		remove_child(c)
		c.queue_free()
	_fields.clear()
	_combat_grid = null

	for item in VIEW_SPEC:
		var k := str(item.get("kind", ""))
		if k == "row":
			var r := HBoxContainer.new()
			r.add_theme_constant_override("separation", 6)
			for f in item["fields"]:
				_add_field(r, f)
			add_child(r)
		elif k == "section":
			add_child(_section_header(str(item["key"])))
		elif k == "separator":
			add_child(HSeparator.new())
		elif k == "combat_grid":
			_combat_grid = NpcEditorCombatGrid.new()
			_combat_grid.set_data(_src)
			add_child(_combat_grid)

	# Hook the render dropdown so the panel's preview viewer can track changes.
	var render_dd = _fields.get("render_slug")
	if render_dd is OptionButton:
		render_dd.item_selected.connect(_on_render_dropdown_changed)


func get_payload() -> Dictionary:
	var data := DevFormFields.collect_payload(_fields, _src)
	if _combat_grid != null:
		var grid_data := _combat_grid.get_payload()
		for key in grid_data.keys():
			data[key] = grid_data[key]
	return data

func _on_render_dropdown_changed(idx: int) -> void:
	var dd = _fields.get("render_slug") as OptionButton
	if dd == null or idx < 0: return
	var md = dd.get_item_metadata(idx)
	render_slug_changed.emit(str(md) if md != null else "")


func get_current_render_slug() -> String:
	var dd = _fields.get("render_slug")
	if dd is OptionButton and dd.selected >= 0:
		var md = dd.get_item_metadata(dd.selected)
		return str(md) if md != null else ""
	return ""

func _section_header(key: String) -> Control:
	var s := DevStyle.instance
	var box := CenterContainer.new()
	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.npc_editor." + key)
	lbl.add_theme_font_size_override("font_size", s.font_size_tab)
	lbl.add_theme_color_override("font_color", s.text_heading)
	box.add_child(lbl)
	return box


func _add_field(row: HBoxContainer, f: Dictionary) -> void:
	var kind := str(f["kind"])
	var value_key := str(f.get("value_key", ""))
	var value: Variant = _src.get(value_key) if value_key != "" else null
	var p := "ui.dev_hub.npc_editor."
	var save_key := str(f["save"])

	if kind == "line_edit":
		var readonly := _is_edit and save_key == "slug_id"
		DevFormFields.compact_line_edit(row, Texts.get_text(p + f["label"]),
			str(value) if value != null else "", readonly, _fields, save_key)
	elif kind == "spin":
		DevFormFields.compact_spin_row(row, Texts.get_text(p + f["label"]),
			int(value) if value != null else 0,
			int(f.get("min", 0)), int(f.get("max", 999999)), _fields, save_key)
	elif kind == "toggle":
		var btn := DevToggleButton.new()
		btn.setup(Texts.get_text(p + f["label"]),
			bool(value) if value != null else false, DevToggleButton.Kind.GREEN)
		row.add_child(btn)
		_fields[save_key] = btn
	elif kind == "textarea":
		DevFormFields.compact_textarea(row, Texts.get_text(p + f["label"]),
			str(value) if value != null else "", _fields, save_key)
	elif kind == "dropdown":
		var opts: Array = []
		if f.has("options_static"):
			opts = (f["options_static"] as Array).duplicate()
		elif f.has("options_key"):
			var raw = _options.get(str(f["options_key"]), [])
			if raw is Array:
				opts = (raw as Array).duplicate()
		if f.has("prepend"):
			opts = (f["prepend"] as Array) + opts
		var l2v: Dictionary = f.get("label_to_value", {})
		DevFormFields.compact_dropdown(row, Texts.get_text(p + f["label"]),
			str(value) if value != null else "", opts, _fields, save_key, l2v)
