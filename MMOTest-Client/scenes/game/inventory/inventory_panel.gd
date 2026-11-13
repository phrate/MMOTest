extends SidePanelTab
## Inventory tab: 4 cols × 7 rows. The credits bar and tab row live in SidePanel.

const COLS := 4
const ROWS := 7

var _grid: GridContainer
var _buttons: Array = []
var _action_menu: ActionMenu
var _action_menu_layer: CanvasLayer
var _context_actions: Array = []

var _overlay_layer: CanvasLayer
var _slot_hover_panel: PanelContainer
var _slot_hover_label: RichTextLabel
var _using_slot: int = -1


func _ready() -> void:
	_build_ui()
	_build_action_menu()
	Inventory.changed.connect(_on_inventory_changed)
	Trade.state_changed.connect(_on_mode_state_changed)
	Bank.state_changed.connect(_on_mode_state_changed)
	_refresh()


func _process(_delta: float) -> void:
	_update_slot_hover()


func _input(event: InputEvent) -> void:
	if _using_slot < 0: return
	if not (event is InputEventMouseButton and event.pressed): return

	if event.button_index == MOUSE_BUTTON_LEFT:
		for i in _buttons.size():
			var btn: InventorySlot = _buttons[i]
			if not is_instance_valid(btn): continue
			if btn.get_global_rect().has_point(event.position):
				if i == _using_slot:
					_cancel_use_mode()
					get_viewport().set_input_as_handled()
					return
				var target: Dictionary = Inventory.get_slot(i)
				if target.is_empty():
					_cancel_use_mode()
					get_viewport().set_input_as_handled()
					return
				Network.send_combine_attempt(_using_slot, i)
				_cancel_use_mode()
				get_viewport().set_input_as_handled()
				return
		# Click outside inventory: cancel use-mode and swallow the click.
		_cancel_use_mode()
		get_viewport().set_input_as_handled()
		return

	if event.button_index == MOUSE_BUTTON_RIGHT:
		for i in _buttons.size():
			var btn2: InventorySlot = _buttons[i]
			if not is_instance_valid(btn2): continue
			if btn2.get_global_rect().has_point(event.position):
				return
		_cancel_use_mode()
		get_viewport().set_input_as_handled()
		return


func _unhandled_input(event: InputEvent) -> void:
	if _using_slot < 0: return
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_ESCAPE:
		_cancel_use_mode()
		get_viewport().set_input_as_handled()


# ---------- UI construction ----------

func _build_ui() -> void:
	_grid = GridContainer.new()
	_grid.columns = COLS
	_grid.add_theme_constant_override("h_separation", 4)
	_grid.add_theme_constant_override("v_separation", 4)
	add_child(_grid)

	for i in ROWS * COLS:
		var btn := InventorySlot.new()
		btn.slot_index = i
		btn.pressed.connect(_on_slot_left_click.bind(i))
		btn.gui_input.connect(_on_slot_input.bind(i))
		btn.swap_requested.connect(_on_swap_requested)
		_grid.add_child(btn)
		_buttons.append(btn)

	# The hover infobox floats above everything on its own layer (it is positioned in screen
	# coordinates, so it can't live inside the panel's layout).
	_overlay_layer = CanvasLayer.new()
	_overlay_layer.name = "HoverLayer"
	_overlay_layer.layer = 14
	add_child(_overlay_layer)

	_slot_hover_panel = PanelContainer.new()
	_slot_hover_panel.name = "SlotHoverPanel"
	_slot_hover_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_slot_hover_panel.visible = false
	_slot_hover_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_overlay_layer.add_child(_slot_hover_panel)

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
	var display_data := _compute_display_inventory()
	for i in _buttons.size():
		var btn: InventorySlot = _buttons[i]
		for c in btn.get_children(): c.queue_free()

		var data: Dictionary = display_data.get(i, {})
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


## Returns {slot_index -> item dict} with offered quantities subtracted from
## the local player's trade offers. When no trade is active, returns the raw
## inventory.
func _compute_display_inventory() -> Dictionary:
	if not Trade.is_active():
		return Inventory.all_slots()

	var offered := {}
	for entry in Trade.my_offer:
		var iid := int(entry.get("item_id", 0))
		offered[iid] = int(offered.get(iid, 0)) + int(entry.get("quantity", 0))

	var out := {}
	var slots := Inventory.all_slots().keys()
	slots.sort()
	for slot_idx in slots:
		var item: Dictionary = Inventory.get_slot(slot_idx)
		if item.is_empty(): continue
		var iid := int(item.get("item_id", 0))
		var qty := int(item.get("quantity", 0))
		var off := int(offered.get(iid, 0))
		if off <= 0:
			out[slot_idx] = item.duplicate(true)
			continue
		var take := mini(off, qty)
		offered[iid] = off - take
		var remaining := qty - take
		if remaining > 0:
			var copy := item.duplicate(true)
			copy["quantity"] = remaining
			out[slot_idx] = copy
	return out


# ---------- Instant hover infobox ----------

func _update_slot_hover() -> void:
	if _slot_hover_panel == null: return

	if not is_visible_in_tree():
		_slot_hover_panel.visible = false
		return

	for m in get_tree().get_nodes_in_group("modal_blockers"):
		if m is CanvasItem and (m as CanvasItem).visible:
			_slot_hover_panel.visible = false
			return

	if _using_slot >= 0:
		_update_use_mode_hover()
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

	var data: Dictionary = _compute_display_inventory().get(hovered_slot, {})
	if data.is_empty():
		_slot_hover_panel.visible = false
		return

	if Bank.is_active():
		var can_bank := bool(data.get("can_bank", true))    # client can't see non_bankable; server enforces
		var name_text := str(data.get("display", ""))
		var qty := int(data.get("quantity", 1))
		var item_text := name_text if qty <= 1 else "%s x%d" % [name_text, qty]
		_slot_hover_label.text = UIStyle.format_primary_bbcode("Deposit " + item_text) if can_bank else item_text
		_slot_hover_panel.reset_size()
		var vp := get_viewport().get_visible_rect().size
		var pos := mouse + Vector2(16, -_slot_hover_panel.size.y - 8)
		pos.x = min(pos.x, vp.x - _slot_hover_panel.size.x - 4)
		pos.y = max(pos.y, 4.0)
		_slot_hover_panel.position = Vector2i(pos)
		_slot_hover_panel.visible = true
		return

	if Trade.is_active():
		var can_offer := Trade.phase == "offering" and bool(data.get("can_trade", true))
		var name_text := str(data.get("display", ""))
		var qty := int(data.get("quantity", 1))
		var item_text := name_text if qty <= 1 else "%s x%d" % [name_text, qty]
		if can_offer:
			_slot_hover_label.text = UIStyle.format_primary_bbcode("Offer " + item_text)
		else:
			_slot_hover_label.text = item_text
		_slot_hover_panel.reset_size()
		var vp := get_viewport().get_visible_rect().size
		var pos := mouse + Vector2(16, -_slot_hover_panel.size.y - 8)
		pos.x = min(pos.x, vp.x - _slot_hover_panel.size.x - 4)
		pos.y = max(pos.y, 4.0)
		_slot_hover_panel.position = Vector2i(pos)
		_slot_hover_panel.visible = true
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
	if Bank.is_active():
		var data: Dictionary = Inventory.get_slot(slot_index)
		if data.is_empty(): return
		var item_id := int(data.get("item_id", 0))
		if item_id > 0:
			Network.send_bank_deposit(Bank.session_id, item_id, 1)
		return
	if Trade.is_active():
		if Trade.phase == "offering":
			var data2: Dictionary = _compute_display_inventory().get(slot_index, {})
			if not data2.is_empty():
				var id2 := int(data2.get("item_id", 0))
				if id2 > 0:
					Network.send_trade_offer_add(Trade.session_id, id2, 1)
		return
	_fire_primary(slot_index)


func _on_swap_requested(from_slot: int, to_slot: int) -> void:
	Network.send_inventory_swap(from_slot, to_slot)


func _on_slot_input(event: InputEvent, slot_index: int) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_RIGHT:
			if _using_slot >= 0:
				_cancel_use_mode()
			if Bank.is_active():
				_show_bank_deposit_menu(slot_index, get_viewport().get_mouse_position())
			elif Trade.is_active():
				if Trade.phase == "offering":
					_show_trade_offer_menu(slot_index, get_viewport().get_mouse_position())
			else:
				_show_context_menu(slot_index, get_viewport().get_mouse_position())
			get_viewport().set_input_as_handled()


func _show_bank_deposit_menu(slot_index: int, pos: Vector2) -> void:
	var data: Dictionary = Inventory.get_slot(slot_index)
	if data.is_empty(): return
	var item_id := int(data.get("item_id", 0))
	if item_id == 0: return
	_context_actions = [
		{"kind": "bank_deposit", "item_id": item_id, "qty": 1,  "label": Texts.get_text("game.bank.deposit_1")},
		{"kind": "bank_deposit", "item_id": item_id, "qty": 10, "label": Texts.get_text("game.bank.deposit_10")},
		{"kind": "bank_deposit", "item_id": item_id, "qty": 100, "label": Texts.get_text("game.bank.deposit_100")},
		{"kind": "bank_deposit", "item_id": item_id, "qty": -2, "label": Texts.get_text("game.bank.deposit_x")},
		{"kind": "bank_deposit", "item_id": item_id, "qty": -1, "label": Texts.get_text("game.bank.deposit_all")},
	]
	var labels: Array = []
	for a in _context_actions: labels.append(str(a["label"]))
	_action_menu.show_actions(labels, pos)


func _show_trade_offer_menu(slot_index: int, pos: Vector2) -> void:
	var data: Dictionary = _compute_display_inventory().get(slot_index, {})
	if data.is_empty(): return
	var item_id := int(data.get("item_id", 0))
	if item_id == 0: return
	_context_actions = [
		{"kind": "trade_offer", "item_id": item_id, "qty": 1,   "label": Texts.get_text("game.trade.offer_1")},
		{"kind": "trade_offer", "item_id": item_id, "qty": 10,  "label": Texts.get_text("game.trade.offer_10")},
		{"kind": "trade_offer", "item_id": item_id, "qty": 100, "label": Texts.get_text("game.trade.offer_100")},
		{"kind": "trade_offer", "item_id": item_id, "qty": -2,  "label": Texts.get_text("game.trade.offer_x")},
		{"kind": "trade_offer", "item_id": item_id, "qty": -1,  "label": Texts.get_text("game.trade.offer_all")},
	]
	var displays: Array = []
	for act in _context_actions:
		displays.append(str(act.get("label", "")))
	_action_menu.show_actions(displays, pos)



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
	if str(act.get("kind", "")) == "bank_deposit":
		var qty := int(act.get("qty", 0))
		var item_id := int(act.get("item_id", 0))
		if qty == -2:
			_open_bank_deposit_amount(item_id)
			return
		Network.send_bank_deposit(Bank.session_id, item_id, qty)
		return
	if str(act.get("kind", "")) == "trade_offer":
		var qty2 := int(act.get("qty", 0))
		var item_id2 := int(act.get("item_id", 0))
		if qty2 == -2:
			Trade.offer_amount_requested.emit(item_id2)
			return
		Network.send_trade_offer_add(Trade.session_id, item_id2, qty2)
		return
	var id := str(act.get("id", ""))
	if id == "use":
		var args: Dictionary = act.get("args", {})
		var slot := int(args.get("slot", -1))
		if slot >= 0:
			_enter_use_mode(slot)
		return
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
	if str(action.get("id", "")) == "use":
		_enter_use_mode(slot_index)
		return
	var script_path: String = str(action.get("script", ""))
	if script_path.is_empty(): return
	Network.run_action(script_path, action.get("args", {}), 0)


func _open_bank_deposit_amount(item_id: int) -> void:
	var dlg := ConfirmationDialog.new()
	dlg.title = "Deposit amount"
	dlg.ok_button_text = "OK"
	dlg.add_cancel_button("Cancel")
	var le := LineEdit.new()
	le.placeholder_text = "0"
	dlg.add_child(le)
	dlg.confirmed.connect(func():
		if le.text.is_valid_int() and int(le.text) > 0:
			Network.send_bank_deposit(Bank.session_id, item_id, int(le.text))
		dlg.queue_free())
	dlg.canceled.connect(func(): dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()
	le.grab_focus()


func _on_inventory_changed() -> void:
	if _using_slot >= 0:
		if Inventory.get_slot(_using_slot).is_empty():
			_cancel_use_mode()
	_refresh()


func _on_mode_state_changed() -> void:
	if _using_slot >= 0 and (Bank.is_active() or Trade.is_active()):
		_cancel_use_mode()
	_refresh()


func _enter_use_mode(slot_index: int) -> void:
	var data: Dictionary = Inventory.get_slot(slot_index)
	if data.is_empty(): return
	_using_slot = slot_index
	_update_use_mode_hover()


func _cancel_use_mode() -> void:
	_using_slot = -1
	_slot_hover_panel.visible = false


func _update_use_mode_hover() -> void:
	if _using_slot < 0: return
	var source: Dictionary = Inventory.get_slot(_using_slot)
	if source.is_empty():
		_cancel_use_mode()
		return

	var source_name := str(source.get("display", "?"))
	var mouse := get_viewport().get_mouse_position()

	var hovered_slot := -1
	for i in _buttons.size():
		var btn: InventorySlot = _buttons[i]
		if not is_instance_valid(btn): continue
		if btn.get_global_rect().has_point(mouse):
			hovered_slot = i
			break

	var target_name := ""
	if hovered_slot >= 0 and hovered_slot != _using_slot:
		var target: Dictionary = Inventory.get_slot(hovered_slot)
		if not target.is_empty():
			target_name = str(target.get("display", ""))

	var blue := UIStyle.ENTITY_COLOR.to_html(false)
	if target_name.is_empty():
		_slot_hover_label.text = "Use [color=%s]%s[/color] on.." % [blue, source_name]
	else:
		_slot_hover_label.text = "Use [color=%s]%s[/color] on [color=%s]%s[/color]" % [blue, source_name, blue, target_name]

	_slot_hover_panel.reset_size()
	var vp := get_viewport().get_visible_rect().size
	var pos := mouse + Vector2(16, -_slot_hover_panel.size.y - 8)
	pos.x = min(pos.x, vp.x - _slot_hover_panel.size.x - 4)
	pos.y = max(pos.y, 4.0)
	_slot_hover_panel.position = Vector2i(pos)
	_slot_hover_panel.visible = true


func tab_deselected() -> void:
	if _using_slot >= 0:
		_cancel_use_mode()
