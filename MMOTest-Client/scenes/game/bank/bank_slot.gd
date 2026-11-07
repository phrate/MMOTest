class_name BankSlot
extends Button
## Bank slot cell. Holds a copy of the entry dict for drag preview purposes.
## Emits move_requested when the player drops another bank slot onto it.

signal move_requested(from: int, to: int, insert: bool)

var slot_index: int = -1
var item_entry: Dictionary = {}


func _get_drag_data(_pos):
	if item_entry.is_empty(): return null
	var wrapper := Control.new()
	wrapper.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var visual := Panel.new()
	visual.custom_minimum_size = Vector2(40, 40)
	visual.size = Vector2(40, 40)
	visual.position = Vector2(-20, -20)
	visual.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.15, 0.15, 0.15, 0.9)
	style.corner_radius_top_left = 3
	style.corner_radius_top_right = 3
	style.corner_radius_bottom_left = 3
	style.corner_radius_bottom_right = 3
	visual.add_theme_stylebox_override("panel", style)

	var is_cert := not str(item_entry.get("cert_of_slug", "")).is_empty()
	var icon := "res://resources/items/icons/brown_cube_icon.png" if is_cert else "res://resources/items/icons/pink_cube_icon.png"
	if ResourceLoader.exists(icon):
		var tex := TextureRect.new()
		tex.texture = load(icon)
		tex.set_anchors_preset(Control.PRESET_FULL_RECT)
		tex.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
		tex.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
		tex.mouse_filter = Control.MOUSE_FILTER_IGNORE
		visual.add_child(tex)
	else:
		var lbl := Label.new()
		lbl.text = str(item_entry.get("display", "?")).substr(0, 1).to_upper()
		lbl.set_anchors_preset(Control.PRESET_FULL_RECT)
		lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		lbl.mouse_filter = Control.MOUSE_FILTER_IGNORE
		visual.add_child(lbl)

	wrapper.add_child(visual)
	set_drag_preview(wrapper)
	return {"source_bank_slot": slot_index}


func _can_drop_data(_pos, data):
	if not (data is Dictionary): return false
	if not data.has("source_bank_slot"): return false
	return int(data["source_bank_slot"]) != slot_index


func _drop_data(_pos, data):
	if not (data is Dictionary): return
	if not data.has("source_bank_slot"): return
	var from := int(data["source_bank_slot"])
	if from == slot_index: return
	move_requested.emit(from, slot_index, Bank.insert_mode)
