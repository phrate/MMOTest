class_name InventorySlot
extends Button
## Single inventory slot. Supports drag-to-swap with other slots.

signal swap_requested(from_slot: int, to_slot: int)

const SLOT_SIZE := 44

var slot_index: int = -1


func _ready() -> void:
	custom_minimum_size = Vector2(SLOT_SIZE, SLOT_SIZE)
	focus_mode = Control.FOCUS_NONE
	clip_text = true
	text = ""


func _get_drag_data(_pos):
	if slot_index < 0: return null
	var data: Dictionary = Inventory.get_slot(slot_index)
	if data.is_empty(): return null

	# Godot places the drag preview's top-left at the cursor. Wrapping the
	# visual in a zero-size Control with the visual offset by -half its size
	# centers the preview on the cursor with no trailing.
	var wrapper := Control.new()
	wrapper.mouse_filter = Control.MOUSE_FILTER_IGNORE

	var visual := Panel.new()
	visual.custom_minimum_size = Vector2(SLOT_SIZE, SLOT_SIZE)
	visual.size = Vector2(SLOT_SIZE, SLOT_SIZE)
	visual.position = Vector2(-SLOT_SIZE / 2.0, -SLOT_SIZE / 2.0)
	visual.mouse_filter = Control.MOUSE_FILTER_IGNORE

	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.15, 0.15, 0.15, 0.9)
	style.corner_radius_top_left = 3
	style.corner_radius_top_right = 3
	style.corner_radius_bottom_left = 3
	style.corner_radius_bottom_right = 3
	visual.add_theme_stylebox_override("panel", style)

	var icon_path := "res://resources/items/icons/pink_cube_icon.png"
	if not str(data.get("cert_of_slug", "")).is_empty():
		icon_path = "res://resources/items/icons/brown_cube_icon.png"
	if ResourceLoader.exists(icon_path):
		var tex := TextureRect.new()
		tex.texture = load(icon_path)
		tex.set_anchors_preset(Control.PRESET_FULL_RECT)
		tex.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
		tex.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
		tex.mouse_filter = Control.MOUSE_FILTER_IGNORE
		visual.add_child(tex)
	else:
		var lbl := Label.new()
		lbl.text = str(data.get("display", "")).substr(0, 1).to_upper()
		lbl.add_theme_font_size_override("font_size", 20)
		lbl.add_theme_constant_override("outline_size", 4)
		lbl.add_theme_color_override("font_outline_color", Color(0, 0, 0))
		lbl.set_anchors_preset(Control.PRESET_FULL_RECT)
		lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		lbl.mouse_filter = Control.MOUSE_FILTER_IGNORE
		visual.add_child(lbl)

	wrapper.add_child(visual)
	set_drag_preview(wrapper)

	return {"source_slot": slot_index}


func _can_drop_data(_pos, data):
	if not (data is Dictionary): return false
	if not data.has("source_slot"): return false
	var src := int(data["source_slot"])
	if src == slot_index: return false
	if src < 0 or src >= 28: return false
	if slot_index < 0 or slot_index >= 28: return false
	return true


func _drop_data(_pos, data):
	var src := int(data["source_slot"])
	swap_requested.emit(src, slot_index)
