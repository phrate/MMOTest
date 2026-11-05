extends CanvasLayer
## Inventory panel — 4 cols × 7 rows bottom-right with a credits box above.

const COLS := 4
const ROWS := 7

var _grid: GridContainer
var _buttons: Array = []
var _action_menu: ActionMenu
var _action_menu_layer: CanvasLayer
var _context_actions: Array = []

var _credits_label: Label
var _slot_hover_panel: PanelContainer
var _slot_hover_label: RichTextLabel


func _ready() -> void:
	layer = 10
	_build_ui()
	_build_action_menu()
	Inventory.changed.connect(_refresh)
	Session.changed.connect(_refresh_credits)
	_refresh()
	_refresh_credits()


func _process(_delta: float) -> void:
	_update_slot_hover()


# ---------- UI construction ----------

func _build_ui() -> void:
	var panel := PanelContainer.new()
	panel.name = "InventoryPanel"
	panel.anchor_left = 1.0
	panel.anchor_right = 1.0
	panel.anchor_top = 1.0
	panel.anchor_bottom = 1.0
	panel.offset_left = -8.0
	panel.offset_right = -8.0
	panel.offset_top = -8.0
	panel.offset_bottom = -8.0
	panel.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	panel.grow_vertical = Control.GROW_DIRECTION_BEGIN

	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.08, 0.08, 0.08, 0.85)
	sb.border_color = Color(0.22, 0.22, 0.22, 1.0)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.content_margin_left = 4
	sb.content_margin_right = 4
	sb.content_margin_top = 4
	sb.content_margin_bottom = 4
	panel.add_theme_stylebox_override("panel", sb)
	add_child(panel)

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 4)
	panel.add_child(vbox)

	var credits_box := PanelContainer.new()
	var cb_style := StyleBoxFlat.new()
	cb_style.bg_color = Color(0.12, 0.10, 0.05, 0.95)
	cb_style.border_color = Color(0.35, 0.28, 0.10, 1.0)
	cb_style.border_width_left = 1
	cb_style.border_width_right = 1
	cb_style.border_width_top = 1
	cb_style.border_width_bottom = 1
	cb_style.content_margin_left = 6
	cb_style.content_margin_right = 6
	cb_style.content_margin_top = 3
	cb_style.content_margin_bottom = 3
	credits_box.add_theme_stylebox_override("panel", cb_style)
	vbox.add_child(credits_box)

	_credits_label = Label.new()
	_credits_label.text = "Ȼ: 0"
	_credits_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_credits_label.add_theme_font_size_override("font_size", 14)
	_credits_label.modulate = Color(1.0, 0.9, 0.5)
	_credits_label.add_theme_constant_override("outline_size", 2)
	_credits_label.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.7))
	credits_box.add_child(_credits_label)

	_grid = GridContainer.new()
	_grid.columns = COLS
	_grid.add_theme_constant_override("h_separation", 4)
	_grid.add_theme_constant_override("v_separation", 4)
	vbox.add_child(_grid)

	for i in ROWS * COLS:
		var btn := InventorySlot.new()
		btn.slot_index = i
		btn.pressed.connect(_on_slot_left_click.bind(i))
		btn.gui_input.connect(_on_slot_input.bind(i))
		btn.swap_requested.connect(_on_swap_requested)
		_grid.add_child(btn)
		_buttons.append(btn)

	_slot_hover_panel = PanelContainer.new()
	_slot_hover_panel.name = "SlotHoverPanel"
	_slot_hover_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_slot_hover_panel.visible = false
	_slot_hover_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	add_child(_slot_hover_panel)

	_slot_hover_label = RichTextLabel.new()
	_slot_hover_label.bbcode_enabled = true
	_slot_hover_label.fit_content = true
	_slot_hover_label.scroll_active = false
	_slot_hover_label.autowrap_mode = TextServer.AUTOWRAP_OFF
	_slot_hover_label.custom_minimum_size = Vector2(60, 0)
	_slot_hover_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_slot_hover_label.add_theme_font_size_override("normal_font_size", UIStyle.FONT_SIZE)
	_slot_hover_label.add_theme_constant_override("outline_size", UIStyle.OUTLINE_SIZE)
	_slot_hover_label.add_theme_color_override("font_outline_color", UIStyle.OUTLINE_COLOR)
	_slot_hover_panel.add_child(_slot_hover_label)


func _build_action_menu() -> void:
	_action_menu_layer = CanvasLayer.new()
	_action_menu_layer.name = "ActionMenuLayer"
	_action_menu_layer.layer = 15
	add_child(_action_menu_layer)

	_action_menu = ActionMenu.new()
	_action_menu.name = "ActionMenu"
	_action_menu_layer.add_child(_action_menu)
	_action_menu.action_selected.connect(_on_action_menu_selected)


# ---------- Refresh ----------

func _refresh() -> void:
	for i in _buttons.size():
		var btn: InventorySlot = _buttons[i]
		for c in btn.get_children(): c.queue_free()

		var data: Dictionary = Inventory.get_slot(i)
		if data.is_empty():
			btn.text = ""
			btn.icon = null
			btn.tooltip_text = ""
			btn.modulate = Color.WHITE
			continue

		btn.modulate = Color.WHITE
		btn.tooltip_text = ""

		var display: String = str(data.get("display", ""))
		btn.text = ""
		var is_cert: bool = not str(data.get("cert_of_slug", "")).is_empty()
		var icon_path := "res://resources/items/icons/pink_cube_icon.png"
		if is_cert:
			icon_path = "res://resources/items/icons/brown_cube_icon.png"
		if ResourceLoader.exists(icon_path):
			btn.icon = load(icon_path)
			btn.expand_icon = false
			btn.add_theme_constant_override("icon_max_width", 32)
		else:
			btn.icon = null
			btn.text = display.substr(0, 1).to_upper()

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


func _refresh_credits() -> void:
	if _credits_label == null: return
	_credits_label.text = "Ȼ: %d" % Session.credits


# ---------- Instant hover infobox ----------

func _update_slot_hover() -> void:
	if _slot_hover_panel == null: return

	for m in get_tree().get_nodes_in_group("modal_blockers"):
		if m is CanvasItem and (m as CanvasItem).visible:
			_slot_hover_panel.visible = false
			return

	if get_viewport().gui_is_dragging():
		_slot_hover_panel.visible = false
		return

	if _action_menu != null and is_instance_valid(_action_menu) and _action_menu.visible:
		_slot_hover_panel.visible = false
		return

	var mouse := get_viewport().get_mouse_position()
	var hovered_slot := -1
	for i in _buttons.size():
		var btn: InventorySlot = _buttons[i]
		if not is_instance_valid(btn): continue
		if btn.get_global_rect().has_point(mouse):
			hovered_slot = i
			break

	if hovered_slot < 0:
		_slot_hover_panel.visible = false
		return

	var data: Dictionary = Inventory.get_slot(hovered_slot)
	if data.is_empty():
		_slot_hover_panel.visible = false
		return

	var key := _tooltip_key_for(data)
	if not Tooltips.has_tooltip_menu(key):
		_slot_hover_panel.visible = false
		return

	var actions: Array = Tooltips.build_actions(key, _item_system_vars(data))
	if actions.is_empty():
		_slot_hover_panel.visible = false
		return

	var display: String = str(actions[0].get("display", ""))
	_slot_hover_label.text = UIStyle.format_primary_bbcode(display)
	_slot_hover_panel.reset_size()

	var vp_size := get_viewport().get_visible_rect().size
	var pos := mouse + Vector2(16, -_slot_hover_panel.size.y - 8)
	pos.x = min(pos.x, vp_size.x - _slot_hover_panel.size.x - 4)
	pos.y = max(pos.y, 4.0)
	_slot_hover_panel.position = Vector2i(pos)
	_slot_hover_panel.visible = true


# ---------- Input ----------

func _on_slot_left_click(slot_index: int) -> void:
	_fire_primary(slot_index)


func _on_swap_requested(from_slot: int, to_slot: int) -> void:
	Network.send_inventory_swap(from_slot, to_slot)


func _on_slot_input(event: InputEvent, slot_index: int) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_RIGHT:
			_show_context_menu(slot_index, get_viewport().get_mouse_position())
			get_viewport().set_input_as_handled()


# ---------- Action menu ----------

func _show_context_menu(slot_index: int, pos: Vector2) -> void:
	var data: Dictionary = Inventory.get_slot(slot_index)
	if data.is_empty(): return
	var key := _tooltip_key_for(data)
	if not Tooltips.has_tooltip_menu(key): return
	var actions: Array = Tooltips.build_actions(key, _item_system_vars(data))
	if actions.is_empty(): return

	_context_actions = actions
	var displays: Array = []
	for act in actions:
		displays.append(str(act.get("display", "")))
	_action_menu.show_actions(displays, pos)


func _on_action_menu_selected(index: int) -> void:
	if index < 0 or index >= _context_actions.size(): return
	var act: Dictionary = _context_actions[index]
	var script_path: String = str(act.get("script", ""))
	if script_path.is_empty(): return
	Network.run_action(script_path, act.get("args", {}), 0)


# ---------- Tooltip evaluation ----------

func _item_system_vars(data: Dictionary) -> Dictionary:
	return {
		"SELF_ID": Session.player_id,
		"ITEM_SLUG": str(data.get("slug", "")),
		"ITEM_DISPLAY_NAME": str(data.get("display", "")),
		"ITEM_SLOT": int(data.get("slot", 0)),
		"ITEM_QUANTITY": int(data.get("quantity", 1)),
		"ITEM_TYPE": str(data.get("type", "")),
		"CERT_OF_SLUG": str(data.get("cert_of_slug", "")),
	}


func _tooltip_key_for(data: Dictionary) -> String:
	if not str(data.get("cert_of_slug", "")).is_empty():
		return "item_cert"
	return "item_" + str(data.get("type", "misc"))


func _fire_primary(slot_index: int) -> void:
	var data: Dictionary = Inventory.get_slot(slot_index)
	if data.is_empty(): return
	var key := _tooltip_key_for(data)
	if not Tooltips.has_tooltip_menu(key): return
	var actions: Array = Tooltips.build_actions(key, _item_system_vars(data))
	if actions.is_empty(): return
	var action: Dictionary = actions[0]
	var script_path: String = str(action.get("script", ""))
	if script_path.is_empty(): return
	Network.run_action(script_path, action.get("args", {}), 0)
