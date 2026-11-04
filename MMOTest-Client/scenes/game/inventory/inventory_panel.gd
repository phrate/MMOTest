extends CanvasLayer
## Inventory panel — 4x7 grid, bottom-right of screen.
## Left-click a slot → primary action (via Tooltips).
## Right-click a slot → tooltip menu with all actions.

const COLS := 7
const ROWS := 4
const SLOT_SIZE := 44
const TYPE_COLORS := {
	"weapon":     Color(0.55, 0.20, 0.20),
	"consumable": Color(0.20, 0.50, 0.20),
	"armor":      Color(0.20, 0.30, 0.55),
	"misc":       Color(0.35, 0.35, 0.35),
}

var _grid: GridContainer
var _buttons: Array = []       # 28 Buttons
var _context_menu: PopupMenu = null
var _context_actions: Array = []


func _ready() -> void:
	layer = 10
	_build_ui()
	Inventory.changed.connect(_refresh)
	_refresh()


func _build_ui() -> void:
	var panel := PanelContainer.new()
	panel.name = "InventoryPanel"
	panel.anchor_left = 1.0
	panel.anchor_right = 1.0
	panel.anchor_top = 1.0
	panel.anchor_bottom = 1.0
	panel.offset_left = -340.0
	panel.offset_right = -8.0
	panel.offset_top = -204.0
	panel.offset_bottom = -8.0
	add_child(panel)

	var vbox := VBoxContainer.new()
	panel.add_child(vbox)

	var title := Label.new()
	title.text = "Inventory"
	title.add_theme_font_size_override("font_size", 14)
	vbox.add_child(title)

	_grid = GridContainer.new()
	_grid.columns = COLS
	vbox.add_child(_grid)

	for i in ROWS * COLS:
		var btn := Button.new()
		btn.custom_minimum_size = Vector2(SLOT_SIZE, SLOT_SIZE)
		btn.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
		btn.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		btn.clip_text = true
		btn.text = ""
		btn.pressed.connect(_on_slot_left_click.bind(i))
		btn.gui_input.connect(_on_slot_input.bind(i))
		_grid.add_child(btn)
		_buttons.append(btn)


func _refresh() -> void:
	for i in _buttons.size():
		var btn: Button = _buttons[i]
		# Clear existing qty label
		for c in btn.get_children():
			c.queue_free()

		var data: Dictionary = Inventory.get_slot(i)
		if data.is_empty():
			btn.text = ""
			btn.modulate = Color(1, 1, 1)
			btn.disabled = true
			continue

		btn.disabled = false
		var item_type: String = str(data.get("type", "misc"))
		var color: Color = TYPE_COLORS.get(item_type, Color(0.35, 0.35, 0.35))
		btn.modulate = color.lightened(0.2)

		var display: String = str(data.get("display", ""))
		btn.text = display.substr(0, 1).to_upper()
		btn.tooltip_text = display

		var qty := int(data.get("quantity", 1))
		if qty > 1:
			var qty_label := Label.new()
			qty_label.text = str(qty)
			qty_label.add_theme_font_size_override("font_size", 11)
			qty_label.modulate = Color(1, 1, 1)
			qty_label.add_theme_constant_override("outline_size", 3)
			qty_label.add_theme_color_override("font_outline_color", Color(0, 0, 0))
			qty_label.anchor_left = 1.0
			qty_label.anchor_right = 1.0
			qty_label.anchor_top = 1.0
			qty_label.anchor_bottom = 1.0
			qty_label.offset_left = -20.0
			qty_label.offset_right = -2.0
			qty_label.offset_top = -18.0
			qty_label.offset_bottom = -2.0
			qty_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
			btn.add_child(qty_label)


# ---------- Click handlers ----------

func _on_slot_left_click(slot_index: int) -> void:
	_fire_primary(slot_index)


func _on_slot_input(event: InputEvent, slot_index: int) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_RIGHT:
			_show_context_menu(slot_index, event.position + _global_offset())
			get_viewport().set_input_as_handled()


func _global_offset() -> Vector2:
	return Vector2.ZERO


# ---------- Tooltip evaluation ----------

func _item_system_vars(data: Dictionary) -> Dictionary:
	return {
		"SELF_ID": Session.player_id,
		"ITEM_SLUG": str(data.get("slug", "")),
		"ITEM_DISPLAY_NAME": str(data.get("display", "")),
		"ITEM_SLOT": int(data.get("slot", 0)),
		"ITEM_QUANTITY": int(data.get("quantity", 1)),
		"ITEM_TYPE": str(data.get("type", "")),
	}


func _tooltip_key_for(item_type: String) -> String:
	return "item_" + item_type


func _fire_primary(slot_index: int) -> void:
	var data: Dictionary = Inventory.get_slot(slot_index)
	if data.is_empty(): return
	var key := _tooltip_key_for(str(data.get("type", "")))
	if not Tooltips.has_tooltip_menu(key): return
	var actions: Array = Tooltips.build_actions(key, _item_system_vars(data))
	if actions.is_empty(): return
	var action: Dictionary = actions[0]
	var script_path: String = str(action.get("script", ""))
	if script_path.is_empty(): return
	Network.run_action(script_path, action.get("args", {}), 0)


func _show_context_menu(slot_index: int, pos: Vector2) -> void:
	var data: Dictionary = Inventory.get_slot(slot_index)
	if data.is_empty(): return
	var key := _tooltip_key_for(str(data.get("type", "")))
	if not Tooltips.has_tooltip_menu(key): return
	var actions: Array = Tooltips.build_actions(key, _item_system_vars(data))
	if actions.is_empty(): return

	if _context_menu == null or not is_instance_valid(_context_menu):
		_context_menu = PopupMenu.new()
		_context_menu.name = "ItemContextMenu"
		add_child(_context_menu)
		_context_menu.id_pressed.connect(_on_context_selected)

	_context_menu.clear()
	_context_actions = actions
	var id := 0
	for act in actions:
		_context_menu.add_item(str(act.get("display", "")), id)
		id += 1
	_context_menu.position = Vector2i(pos)
	_context_menu.popup()


func _on_context_selected(id: int) -> void:
	if id < 0 or id >= _context_actions.size(): return
	var act: Dictionary = _context_actions[id]
	var script_path: String = str(act.get("script", ""))
	if script_path.is_empty(): return
	Network.run_action(script_path, act.get("args", {}), 0)
