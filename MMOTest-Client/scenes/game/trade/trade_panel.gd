extends CanvasLayer

const PANEL_SIZE := Vector2(740, 540)

var _overlay: Control
var _center: CenterContainer
var _panel: PanelContainer
var _title: Label
var _error_label: Label
var _error_message := ""
var _body: VBoxContainer
var _invite: Dictionary = {}
var _trade: Dictionary = {}
var _selected_slot := -1
var _inventory_list: ItemList
var _quantity: SpinBox
var _credits: SpinBox


func _ready() -> void:
	layer = 30
	_build_panel()
	Network.trade_request_incoming.connect(_on_trade_request_incoming)
	Network.trade_request_cancelled.connect(_on_trade_request_cancelled)
	Network.trade_state_received.connect(_on_trade_state_received)
	Network.trade_error.connect(_on_trade_error)
	Network.trade_cancelled.connect(_on_trade_cancelled)
	Network.trade_completed.connect(_on_trade_completed)
	Network.state_changed.connect(_on_network_state_changed)
	Inventory.changed.connect(_on_inventory_changed)


func _build_panel() -> void:
	_overlay = Control.new()
	_overlay.name = "TradeModal"
	_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_overlay.mouse_filter = Control.MOUSE_FILTER_STOP
	_overlay.visible = false
	_overlay.add_to_group("modal_blockers")
	add_child(_overlay)

	var shade := ColorRect.new()
	shade.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	shade.color = Color(0, 0, 0, 0.55)
	shade.mouse_filter = Control.MOUSE_FILTER_STOP
	_overlay.add_child(shade)

	_center = CenterContainer.new()
	_center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_overlay.add_child(_center)

	_panel = PanelContainer.new()
	_panel.custom_minimum_size = PANEL_SIZE
	_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_center.add_child(_panel)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 12)
	margin.add_theme_constant_override("margin_right", 12)
	margin.add_theme_constant_override("margin_top", 10)
	margin.add_theme_constant_override("margin_bottom", 10)
	_panel.add_child(margin)

	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 8)
	margin.add_child(column)

	var header := HBoxContainer.new()
	column.add_child(header)
	_title = Label.new()
	_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_title.add_theme_font_size_override("font_size", 18)
	header.add_child(_title)
	var cancel_button := Button.new()
	cancel_button.text = _text("cancel")
	cancel_button.pressed.connect(_on_cancel_pressed)
	header.add_child(cancel_button)

	_error_label = Label.new()
	_error_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_error_label.add_theme_color_override("font_color", Color(1.0, 0.55, 0.4))
	column.add_child(_error_label)

	_body = VBoxContainer.new()
	_body.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_body.add_theme_constant_override("separation", 8)
	column.add_child(_body)


func _text(key: String, params: Dictionary = {}) -> String:
	return Texts.get_text("game.trade." + key, params)


func _render() -> void:
	_center.offset_top = 0.0
	_center.offset_bottom = 0.0
	_panel.custom_minimum_size = PANEL_SIZE
	for child in _body.get_children():
		_body.remove_child(child)
		child.queue_free()
	_inventory_list = null
	_quantity = null
	_credits = null

	if not _invite.is_empty():
		var vertical_offset := _overlay.get_viewport_rect().size.y / 6.0
		_center.offset_top = -vertical_offset
		_center.offset_bottom = -vertical_offset
		_panel.custom_minimum_size = Vector2(460, 180)
		_title.text = _text("title")
		var prompt := Label.new()
		prompt.text = _text("incoming", {"username": str(_invite.get("username", ""))})
		_body.add_child(prompt)
		var buttons := HBoxContainer.new()
		buttons.add_theme_constant_override("separation", 8)
		_body.add_child(buttons)
		var accept := Button.new()
		var responding := bool(_invite.get("pending", false))
		accept.text = _text("waiting") if responding else _text("accept_request")
		accept.disabled = responding
		accept.pressed.connect(_on_invite_accept)
		buttons.add_child(accept)
		var decline := Button.new()
		decline.text = _text("deny")
		decline.disabled = responding
		decline.pressed.connect(_on_invite_decline)
		buttons.add_child(decline)
		_overlay.visible = true
		return

	if _trade.is_empty():
		if _error_message.is_empty():
			_overlay.visible = false
			return
		_title.text = _text("title")
		_body.add_child(_make_label(_error_message))
		_overlay.visible = true
		return

	_title.text = _text("title_with_player", {"username": str(_trade.get("partner_username", ""))})
	var phase := str(_trade.get("phase", "offering"))
	_body.add_child(_make_label(_text("phase_" + phase)))

	var columns := HBoxContainer.new()
	columns.size_flags_vertical = Control.SIZE_EXPAND_FILL
	columns.add_theme_constant_override("separation", 12)
	_body.add_child(columns)

	var own_column := VBoxContainer.new()
	own_column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	own_column.size_flags_vertical = Control.SIZE_EXPAND_FILL
	columns.add_child(own_column)
	own_column.add_child(_make_label(_text("your_inventory")))
	_inventory_list = ItemList.new()
	_inventory_list.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_inventory_list.custom_minimum_size = Vector2(300, 180)
	_inventory_list.item_selected.connect(_on_inventory_selection_changed)
	own_column.add_child(_inventory_list)
	_fill_inventory_list()

	var offer_controls := HBoxContainer.new()
	own_column.add_child(offer_controls)
	_quantity = SpinBox.new()
	_quantity.min_value = 0
	_quantity.step = 1
	_quantity.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_offer_selected_slot()
	offer_controls.add_child(_quantity)
	var offer_button := Button.new()
	offer_button.text = _text("set_offer")
	offer_button.pressed.connect(_on_set_offer_pressed)
	offer_controls.add_child(offer_button)

	own_column.add_child(_make_label(_text("your_offer")))
	own_column.add_child(_make_label(_format_offer(_trade.get("my_offer", []))))
	var credits_row := HBoxContainer.new()
	_credits = SpinBox.new()
	_credits.min_value = 0
	_credits.max_value = maxi(0, Session.credits)
	_credits.step = 1
	_credits.value = int(_trade.get("my_credits", 0))
	_credits.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	credits_row.add_child(_credits)
	var credits_button := Button.new()
	credits_button.text = _text("set_credits")
	credits_button.pressed.connect(_on_set_credits_pressed)
	credits_row.add_child(credits_button)
	own_column.add_child(credits_row)

	var other_column := VBoxContainer.new()
	other_column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	other_column.size_flags_vertical = Control.SIZE_EXPAND_FILL
	columns.add_child(other_column)
	other_column.add_child(_make_label(_text("their_offer")))
	var their_items := Label.new()
	their_items.text = _format_offer(_trade.get("their_offer", []))
	their_items.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	their_items.size_flags_vertical = Control.SIZE_EXPAND_FILL
	other_column.add_child(their_items)
	other_column.add_child(_make_label(_text("their_credits", {"amount": NumberText.with_commas(int(_trade.get("their_credits", 0)))})))

	var acceptance := Label.new()
	acceptance.text = _text("acceptance_status", {
		"you": _text("yes") if bool(_trade.get("my_accepted", false)) else _text("no"),
		"them": _text("yes") if bool(_trade.get("their_accepted", false)) else _text("no"),
	})
	_body.add_child(acceptance)
	var actions := HBoxContainer.new()
	actions.add_theme_constant_override("separation", 8)
	_body.add_child(actions)
	if phase == "offering":
		var accept_offer := Button.new()
		accept_offer.text = _text("accept_offer") if not bool(_trade.get("my_accepted", false)) else _text("waiting")
		accept_offer.disabled = bool(_trade.get("my_accepted", false))
		accept_offer.pressed.connect(_on_accept_offer_pressed)
		actions.add_child(accept_offer)
	else:
		var confirm := Button.new()
		confirm.text = _text("confirm")
		confirm.disabled = bool(_trade.get("my_confirmed", false))
		confirm.pressed.connect(_on_confirm_pressed)
		actions.add_child(confirm)
		var edit := Button.new()
		edit.text = _text("edit_offer")
		edit.pressed.connect(_on_edit_offer_pressed)
		actions.add_child(edit)

	if not _error_message.is_empty():
		_error_label.text = _error_message
	_overlay.visible = true


func _make_label(value: String) -> Label:
	var label := Label.new()
	label.text = value
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	return label


func _fill_inventory_list() -> void:
	if _inventory_list == null:
		return
	var slots := Inventory.all_slots().keys()
	slots.sort()
	for slot_value in slots:
		var slot := int(slot_value)
		var item: Dictionary = Inventory.get_slot(slot)
		var quantity := int(item.get("quantity", 0))
		var offered := _offered_quantity(slot)
		var line := "%s x%d" % [str(item.get("display", item.get("slug", "?"))), quantity]
		if offered > 0:
			line += " (%s %d)" % [_text("offered_short"), offered]
		var index := _inventory_list.add_item(line)
		_inventory_list.set_item_metadata(index, slot)
		if slot == _selected_slot:
			_inventory_list.select(index)


func _offer_selected_slot() -> void:
	if _quantity == null:
		return
	var item := Inventory.get_slot(_selected_slot)
	var available := int(item.get("quantity", 0))
	_quantity.max_value = available
	var offered := _offered_quantity(_selected_slot)
	_quantity.value = minf(float(offered if offered > 0 else available), float(available))


func _offered_quantity(slot: int) -> int:
	for offer_value in _trade.get("my_offer", []):
		if typeof(offer_value) == TYPE_DICTIONARY and int(offer_value.get("slot", -1)) == slot:
			return int(offer_value.get("quantity", 0))
	return 0


func _format_offer(offer: Variant) -> String:
	if typeof(offer) != TYPE_ARRAY or offer.is_empty():
		return _text("empty_offer")
	var lines: Array[String] = []
	for entry in offer:
		if typeof(entry) != TYPE_DICTIONARY:
			continue
		lines.append("%s x%d" % [str(entry.get("display", entry.get("slug", "?"))), int(entry.get("quantity", 0))])
	return "\n".join(lines) if not lines.is_empty() else _text("empty_offer")


func _on_trade_request_incoming(invite_id: String, from_player_id: int, from_username: String) -> void:
	_invite = {"invite_id": invite_id, "player_id": from_player_id, "username": from_username}
	_trade.clear()
	_error_message = ""
	_render()


func _on_trade_request_cancelled(invite_id: String, reason: String) -> void:
	if not _invite.is_empty() and str(_invite.get("invite_id", "")) == invite_id:
		_invite.clear()
		_error_message = reason
		_render()


func _on_trade_state_received(trade_state: Dictionary) -> void:
	_trade = trade_state.duplicate(true)
	_invite.clear()
	_error_message = ""
	_render()


func _on_trade_error(message: String) -> void:
	_error_message = message
	_render()


func _on_trade_cancelled(session_id: String, reason: String) -> void:
	if _trade.is_empty() or str(_trade.get("session_id", "")) == session_id:
		_trade.clear()
		_invite.clear()
		_error_message = reason
		_render()


func _on_trade_completed(session_id: String) -> void:
	if _trade.is_empty() or str(_trade.get("session_id", "")) == session_id:
		_trade.clear()
		_invite.clear()
		_error_message = ""
		_render()


func _on_network_state_changed(state: int) -> void:
	if state == Network.State.DISCONNECTED:
		_trade.clear()
		_invite.clear()
		_error_message = ""
		_render()


func _on_inventory_changed() -> void:
	if not _trade.is_empty():
		_render()


func _on_inventory_selection_changed(index: int) -> void:
	_selected_slot = int(_inventory_list.get_item_metadata(index))
	_offer_selected_slot()


func _on_invite_accept() -> void:
	if _invite.is_empty() or bool(_invite.get("pending", false)):
		return
	_invite["pending"] = true
	Network.respond_to_trade_invite(str(_invite.get("invite_id", "")), true)
	_render()


func _on_invite_decline() -> void:
	Network.respond_to_trade_invite(str(_invite.get("invite_id", "")), false)
	_invite.clear()
	_render()


func _on_set_offer_pressed() -> void:
	if _selected_slot >= 0 and _quantity != null:
		Network.send_trade_offer(str(_trade.get("session_id", "")), _selected_slot, int(_quantity.value))


func _on_set_credits_pressed() -> void:
	if _credits != null:
		Network.send_trade_credits(str(_trade.get("session_id", "")), int(_credits.value))


func _on_accept_offer_pressed() -> void:
	Network.accept_trade_offer(str(_trade.get("session_id", "")))


func _on_edit_offer_pressed() -> void:
	Network.edit_trade_offer(str(_trade.get("session_id", "")))


func _on_confirm_pressed() -> void:
	Network.confirm_trade(str(_trade.get("session_id", "")))


func _on_cancel_pressed() -> void:
	if not _trade.is_empty():
		Network.cancel_trade(str(_trade.get("session_id", "")))
	elif not _invite.is_empty():
		_on_invite_decline()
	else:
		_overlay.visible = false