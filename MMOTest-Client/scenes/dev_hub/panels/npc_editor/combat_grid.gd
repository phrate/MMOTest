class_name NpcEditorCombatGrid
extends VBoxContainer
## Fixed-layout 4-column combat stats grid. Rows wrap at 4 cells; partial
## rows pad with spacers so columns stay aligned across all rows.

const COLS := 4
const CELL_WIDTH := 151
const LABEL_WIDTH := 85
const SPIN_WIDTH := 62
const CELL_SEP := 12
const LABEL_SPIN_SEP := 4

var fields: Dictionary = {}
var _src: Dictionary


func _init() -> void:
	add_theme_constant_override("separation", 4)


func set_data(src: Dictionary) -> void:
	_src = src
	_rebuild()


func _rebuild() -> void:
	for c in get_children():
		remove_child(c)
		c.queue_free()
	fields.clear()

	_add_row([
		_spec("label_attack_speed", "attack_speed", _i("attack_speed", 10), 1, 999),
		_spec("label_aggro_range", "aggro_range", _i("aggro_range", 8), 1, 999),
		_spec("label_spawn_timer", "spawn_timer", _i("spawn_timer", 3), 0, 999999),
	])
	_add_row([
		_spec("label_final_level", "final_level", _i("final_level", 1), 1, 999),
	])
	_add_row([
		_spec("label_hitpoints", "max_hitpoints", _i("max_hitpoints", 10), 1, 999999),
	])
	_add_row([
		_spec("label_accuracy", "max_accuracy", _i("max_accuracy", 1), 1, 999999),
		_spec("label_strength", "max_strength", _i("max_strength", 1), 1, 999999),
		_spec("label_defence", "max_defence", _i("max_defence", 1), 1, 999999),
		_spec("label_ether", "max_ether", _i("max_ether", 1), 1, 999999),
	])
	_add_row([
		_spec("label_melee_bonus", "melee_bonus", _i("melee_bonus", 0), 0, 999999),
		_spec("label_ranged_bonus", "ranged_bonus", _i("ranged_bonus", 0), 0, 999999),
		_spec("label_ether_bonus", "ether_bonus", _i("ether_bonus", 0), 0, 999999),
	])
	_add_row([
		_spec("label_melee_defense", "melee_defense", _i("melee_defense", 0), 0, 999999),
		_spec("label_ranged_defense", "ranged_defense", _i("ranged_defense", 0), 0, 999999),
		_spec("label_ether_defense", "ether_defense", _i("ether_defense", 0), 0, 999999),
	])


func get_payload() -> Dictionary:
	var out := {}
	for key in fields.keys():
		var w = fields[key]
		if w is SpinBox:
			out[key] = int((w as SpinBox).value)
	return out


func _i(key: String, def: int) -> int:
	return int(_src.get(key, def))


func _spec(label_key: String, save_key: String, val: int, minv: int, maxv: int) -> Dictionary:
	return {"label": label_key, "key": save_key, "val": val, "min": minv, "max": maxv}


func _add_row(cells: Array) -> void:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", CELL_SEP)
	add_child(row)
	for i in COLS:
		if i < cells.size():
			_add_cell(row, cells[i])
		else:
			var sp := Control.new()
			sp.custom_minimum_size = Vector2(CELL_WIDTH, 0)
			row.add_child(sp)


func _add_cell(row: HBoxContainer, spec: Dictionary) -> void:
	var s := DevStyle.instance
	var cell := HBoxContainer.new()
	cell.custom_minimum_size = Vector2(CELL_WIDTH, 0)
	cell.add_theme_constant_override("separation", LABEL_SPIN_SEP)

	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.npc_editor." + spec["label"])
	lbl.custom_minimum_size = Vector2(LABEL_WIDTH, 0)
	lbl.clip_text = true
	lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	lbl.add_theme_font_size_override("font_size", s.font_size_small)
	lbl.add_theme_color_override("font_color", s.text_muted)
	cell.add_child(lbl)

	var sb := SpinBox.new()
	sb.min_value = spec["min"]
	sb.max_value = spec["max"]
	sb.step = 1
	sb.value = spec["val"]
	sb.custom_minimum_size = Vector2(SPIN_WIDTH, 0)
	sb.add_theme_font_size_override("font_size", s.font_size_small)
	cell.add_child(sb)

	row.add_child(cell)
	fields[spec["key"]] = sb