class_name PlayerEditorSlotsGrid
extends GridContainer
## Read-only grid of item slots. Used for both the inventory (4 cols, 44px)
## and the bank (8 cols, 40px) — visually matches the real game grids.
##
## Hover behavior: emits hover_show(text) / hover_hide() so the form can
## display an infobox identical to the rest of the game, minus actions.

signal hover_show(text: String)
signal hover_hide()

var _slot_size: int = 44
var _total_slots: int = 0
var _items_by_slot: Dictionary = {}


func _init() -> void:
	add_theme_constant_override("h_separation", 2)
	add_theme_constant_override("v_separation", 2)


func configure(cols: int, slot_size: int) -> void:
	columns = cols
	_slot_size = slot_size


func set_total_slots(n: int) -> void:
	_total_slots = n
	_rebuild()


func set_items(items: Array) -> void:
	_items_by_slot.clear()
	for entry in items:
		if typeof(entry) != TYPE_DICTIONARY: continue
		var slot := int(entry.get("slot", -1))
		if slot < 0 or slot >= _total_slots: continue
		_items_by_slot[slot] = entry
	_rebuild()


func _rebuild() -> void:
	for c in get_children():
		remove_child(c)
		c.queue_free()

	for i in _total_slots:
		var btn := Button.new()
		btn.custom_minimum_size = Vector2(_slot_size, _slot_size)
		btn.focus_mode = Control.FOCUS_NONE
		btn.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
		btn.vertical_icon_alignment = VERTICAL_ALIGNMENT_CENTER
		btn.add_theme_stylebox_override("normal", _slot_style())
		btn.add_theme_stylebox_override("hover", _slot_style())
		btn.add_theme_stylebox_override("pressed", _slot_style())
		btn.add_theme_stylebox_override("focus", StyleBoxEmpty.new())
		add_child(btn)

		var entry = _items_by_slot.get(i)
		if entry != null:
			_populate(btn, entry)
			btn.mouse_entered.connect(_on_hover.bind(entry))
			btn.mouse_exited.connect(func() -> void: hover_hide.emit())


func _slot_style() -> StyleBoxFlat:
	var s := StyleBoxFlat.new()
	s.bg_color = Color(0.18, 0.18, 0.18, 1.0)
	s.border_color = Color(0.32, 0.32, 0.32, 1.0)
	s.border_width_left = 1
	s.border_width_right = 1
	s.border_width_top = 1
	s.border_width_bottom = 1
	s.corner_radius_top_left = 3
	s.corner_radius_top_right = 3
	s.corner_radius_bottom_left = 3
	s.corner_radius_bottom_right = 3
	return s


func _populate(btn: Button, entry: Dictionary) -> void:
	var display := str(entry.get("display", ""))
	if display.is_empty():
		display = "Item %d" % int(entry.get("item_id", 0))
	var is_cert := not str(entry.get("cert_of_slug", "")).is_empty()
	var icon := "res://resources/items/icons/brown_cube_icon.png" if is_cert else "res://resources/items/icons/pink_cube_icon.png"
	if ResourceLoader.exists(icon):
		btn.icon = load(icon)
		btn.expand_icon = false
		btn.add_theme_constant_override("icon_max_width", maxi(20, _slot_size - 12))
	else:
		btn.text = display.substr(0, 1).to_upper()

	var qty := int(entry.get("quantity", 1))
	if qty > 1:
		var lbl := Label.new()
		lbl.text = str(qty)
		lbl.add_theme_font_size_override("font_size", 11)
		lbl.add_theme_constant_override("outline_size", 3)
		lbl.add_theme_color_override("font_outline_color", Color(0, 0, 0))
		lbl.anchor_left = 1.0
		lbl.anchor_right = 1.0
		lbl.anchor_top = 1.0
		lbl.anchor_bottom = 1.0
		lbl.offset_left = -18.0
		lbl.offset_right = -2.0
		lbl.offset_top = -16.0
		lbl.offset_bottom = -2.0
		lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
		lbl.mouse_filter = Control.MOUSE_FILTER_IGNORE
		btn.add_child(lbl)


func _on_hover(entry: Dictionary) -> void:
	var display := str(entry.get("display", ""))
	if display.is_empty():
		display = "Item %d" % int(entry.get("item_id", 0))
	var qty := int(entry.get("quantity", 1))
	var text := Texts.get_text("ui.dev_hub.player_editor.hover_item", {"name": display}) if qty <= 1 \
		else Texts.get_text("ui.dev_hub.player_editor.hover_item_qty", {"name": display, "qty": qty})
	hover_show.emit(text)
