extends CanvasLayer
## Bank panel. Bank grid on the left, side-panel inventory stays active on the
## right for depositing. Swap/Insert toggle changes drag semantics. Cert toggle
## affects withdrawal mode (per session, resets on close).

const PANEL_WIDTH := 420.0
const SLOT_SIZE := Vector2(40, 40)
const COLS := 8
const MAX_VISIBLE_ROWS := 14

const OUTLINE_COLOR_NONE := Color(0, 0, 0, 0)
const OUTLINE_COLOR_GREEN := Color(0.2, 0.9, 0.2, 1.0)
const OUTLINE_WIDTH := 2

const SLOT_BG_COLOR     := Color(0.18, 0.18, 0.18, 1.0)
const SLOT_BORDER_COLOR := Color(0.32, 0.32, 0.32, 1.0)

const BUTTON_GREEN := Color(0.25, 0.62, 0.25)
const BUTTON_RED   := Color(0.62, 0.20, 0.20)

var _overlay: Control
var _panel: PanelContainer
var _title: Label
var _grid: GridContainer
var _scroll: ScrollContainer
var _swap_mode: bool = true     # true = swap, false = insert
var _context_actions: Array = []
var _cert_btn: Button
var _swap_btn: Button

var _action_menu_layer: CanvasLayer
var _action_menu: ActionMenu

var _hover_layer: CanvasLayer
var _hover_panel: PanelContainer
var _hover_label: RichTextLabel


func _ready() -> void:
	layer = 30
	_build_ui()
	_build_action_menu()
	_build_hover()
	Bank.state_changed.connect(_on_state)
	Bank.closed.connect(_on_closed)
	Bank.notice_received.connect(_noop_notice)
	Network.state_changed.connect(_on_network_state)


func _noop_notice(_text: String) -> void: pass


func _process(_d: float) -> void:
	if _hover_panel.visible: _update_hover_position()


func _input(event: InputEvent) -> void:
	if not Bank.is_active() or _overlay == null or not _overlay.visible:
		return
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_WHEEL_UP or event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			if _panel.get_global_rect().has_point(event.position):
				var step := 48
				if event.button_index == MOUSE_BUTTON_WHEEL_UP:
					_scroll.scroll_vertical = maxi(0, _scroll.scroll_vertical - step)
				else:
					_scroll.scroll_vertical += step
				get_viewport().set_input_as_handled()


func _build_ui() -> void:
	_overlay = Control.new()
	_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_overlay.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_overlay.visible = false
	add_child(_overlay)

	var tint := ColorRect.new()
	tint.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	tint.color = Color(0, 0, 0, 0.12)
	tint.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_overlay.add_child(tint)

	_panel = PanelContainer.new()
	_panel.anchor_left = 0.5
	_panel.anchor_right = 0.5
	_panel.anchor_top = 0.0
	_panel.anchor_bottom = 1.0
	_panel.offset_left = -PANEL_WIDTH / 2.0
	_panel.offset_right = PANEL_WIDTH / 2.0
	_panel.offset_top = 20.0
	_panel.offset_bottom = -20.0
	_panel.grow_horizontal = Control.GROW_DIRECTION_BOTH
	_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_overlay.add_child(_panel)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 12)
	margin.add_theme_constant_override("margin_right", 12)
	margin.add_theme_constant_override("margin_top", 10)
	margin.add_theme_constant_override("margin_bottom", 10)
	_panel.add_child(margin)

	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", 8)
	margin.add_child(col)

	# Header row: title + close on the right
	var header := HBoxContainer.new()
	header.add_theme_constant_override("separation", 6)
	col.add_child(header)

	_title = Label.new()
	_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_title.add_theme_font_size_override("font_size", 16)
	header.add_child(_title)

	var close_btn := _make_action_button(_text("close_button"), BUTTON_RED)
	close_btn.pressed.connect(_on_close_pressed)
	header.add_child(close_btn)

	# Scrollable grid
	_scroll = ScrollContainer.new()
	_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_scroll.custom_minimum_size = Vector2(0, 150)
	col.add_child(_scroll)

	_grid = GridContainer.new()
	_grid.columns = COLS
	_grid.add_theme_constant_override("h_separation", 2)
	_grid.add_theme_constant_override("v_separation", 2)
	_scroll.add_child(_grid)

	# Bottom row: cert + swap on the left
	var bottom := HBoxContainer.new()
	bottom.add_theme_constant_override("separation", 6)
	col.add_child(bottom)

	_cert_btn = _make_toggle_button(_text("cert_toggle"))
	_cert_btn.toggled.connect(_on_cert_toggled)
	bottom.add_child(_cert_btn)

	_swap_btn = _make_mode_button(_text("swap_mode"))
	_swap_btn.pressed.connect(_on_swap_pressed)
	bottom.add_child(_swap_btn)

	_grid = GridContainer.new()
	_grid.columns = COLS
	_grid.add_theme_constant_override("h_separation", 2)
	_grid.add_theme_constant_override("v_separation", 2)
	_scroll.add_child(_grid)


func _build_action_menu() -> void:
	_action_menu_layer = CanvasLayer.new()
	_action_menu_layer.layer = 31
	add_child(_action_menu_layer)
	_action_menu = ActionMenu.new()
	_action_menu_layer.add_child(_action_menu)
	_action_menu.action_selected.connect(_on_action_menu_selected)


func _build_hover() -> void:
	_hover_layer = CanvasLayer.new()
	_hover_layer.layer = 32
	add_child(_hover_layer)
	_hover_panel = PanelContainer.new()
	_hover_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_panel.visible = false
	_hover_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_hover_layer.add_child(_hover_panel)

	_hover_label = RichTextLabel.new()
	_hover_label.bbcode_enabled = true
	_hover_label.fit_content = true
	_hover_label.scroll_active = false
	_hover_label.autowrap_mode = TextServer.AUTOWRAP_OFF
	_hover_label.custom_minimum_size = Vector2(60, 0)
	_hover_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_label.add_theme_font_size_override("normal_font_size", UIStyle.FONT_SIZE)
	_hover_label.add_theme_constant_override("outline_size", UIStyle.OUTLINE_SIZE)
	_hover_label.add_theme_color_override("font_outline_color", UIStyle.OUTLINE_COLOR)
	_hover_panel.add_child(_hover_label)


# ---------- State ----------

func _on_state() -> void:
	_overlay.visible = Bank.is_active()
	if not Bank.is_active(): return
	_title.text = _text("capacity", {"used": Bank.used_slots(), "capacity": Bank.capacity})
	if _cert_btn != null:
		_cert_btn.set_pressed_no_signal(Bank.cert_mode)
	_render_grid()


func _on_closed(error_key: String, error_params: Dictionary) -> void:
	_overlay.visible = false
	Bank.clear()
	if not error_key.is_empty():
		var sub := error_key.trim_prefix("bank.")
		Network.emit_system_message(Texts.get_text("game.bank." + sub, error_params))


func _on_network_state(state: int) -> void:
	if state == Network.State.DISCONNECTED:
		_overlay.visible = false


func _render_grid() -> void:
	for c in _grid.get_children():
		_grid.remove_child(c)
		c.queue_free()

	for i in Bank.capacity:
		var entry = Bank.slots[i] if i < Bank.slots.size() else null
		var slot := BankSlot.new()
		slot.slot_index = i
		slot.custom_minimum_size = SLOT_SIZE
		slot.focus_mode = Control.FOCUS_NONE
		slot.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
		slot.vertical_icon_alignment = VERTICAL_ALIGNMENT_CENTER
		slot.add_theme_stylebox_override("normal", _slot_style())
		slot.add_theme_stylebox_override("hover", _slot_style())
		slot.add_theme_stylebox_override("pressed", _slot_style())
		slot.add_theme_stylebox_override("focus", StyleBoxEmpty.new())

		if entry != null:
			slot.item_entry = entry
			_populate_slot(slot, entry)
			slot.pressed.connect(_on_slot_pressed.bind(i))
			slot.gui_input.connect(_on_slot_gui_input.bind(i))
			slot.mouse_entered.connect(_on_slot_hover.bind(entry, true))
			slot.mouse_exited.connect(_on_slot_hover_exit)
			slot.move_requested.connect(_on_slot_move_requested)
		else:
			# Empty slots accept drops but can't be dragged from.
			slot.move_requested.connect(_on_slot_move_requested)
			slot.pressed.connect(_noop_press.bind(i))
		_grid.add_child(slot)


func _on_slot_gui_input(event: InputEvent, index: int) -> void:
	if not (event is InputEventMouseButton and event.pressed): return
	if event.button_index == MOUSE_BUTTON_RIGHT:
		_show_withdraw_menu(index)
		get_viewport().set_input_as_handled()


func _on_slot_move_requested(from: int, to: int, insert: bool) -> void:
	Network.send_bank_move(Bank.session_id, from, to, insert)


func _on_swap_pressed() -> void:
	_swap_mode = not _swap_mode
	Bank.insert_mode = not _swap_mode
	_swap_btn.text = _text("swap_mode") if _swap_mode else _text("insert_mode")


func _noop_press(_i: int) -> void: pass


func _on_slot_pressed(index: int) -> void:
	# Fires only on clean click (drag cancels this signal).
	var entry = Bank.slots[index]
	if entry == null: return
	Network.send_bank_withdraw(Bank.session_id, int(entry.get("item_id", 0)), 1)


func _slot_style() -> StyleBoxFlat:
	var style := StyleBoxFlat.new()
	style.bg_color = SLOT_BG_COLOR
	style.border_color = SLOT_BORDER_COLOR
	style.border_width_left = 1
	style.border_width_right = 1
	style.border_width_top = 1
	style.border_width_bottom = 1
	style.corner_radius_top_left = 3
	style.corner_radius_top_right = 3
	style.corner_radius_bottom_left = 3
	style.corner_radius_bottom_right = 3
	return style


func _make_slot() -> Button:
	var btn := Button.new()
	btn.custom_minimum_size = SLOT_SIZE
	btn.focus_mode = Control.FOCUS_NONE
	btn.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
	btn.vertical_icon_alignment = VERTICAL_ALIGNMENT_CENTER
	var style := StyleBoxFlat.new()
	style.bg_color = SLOT_BG_COLOR
	style.border_color = SLOT_BORDER_COLOR
	style.border_width_left = 1
	style.border_width_right = 1
	style.border_width_top = 1
	style.border_width_bottom = 1
	style.corner_radius_top_left = 3
	style.corner_radius_top_right = 3
	style.corner_radius_bottom_left = 3
	style.corner_radius_bottom_right = 3
	btn.add_theme_stylebox_override("normal", style)
	btn.add_theme_stylebox_override("hover", style)
	btn.add_theme_stylebox_override("pressed", style)
	btn.add_theme_stylebox_override("focus", StyleBoxEmpty.new())
	return btn


func _populate_slot(btn: Button, entry: Dictionary) -> void:
	var item_id := int(entry.get("item_id", 0))
	var qty := int(entry.get("quantity", 1))
	var display := str(entry.get("display", ""))
	if display.is_empty():
		display = "Item %d" % item_id
	var is_cert := not str(entry.get("cert_of_slug", "")).is_empty()
	var icon := "res://resources/items/icons/brown_cube_icon.png" if is_cert else "res://resources/items/icons/pink_cube_icon.png"
	if ResourceLoader.exists(icon):
		btn.icon = load(icon)
		btn.expand_icon = false
		btn.add_theme_constant_override("icon_max_width", 32)
	else:
		btn.text = display.substr(0, 1).to_upper()
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


func _lookup_item(item_id: int) -> Dictionary:
	return {}   # superseded; entries now carry display directly


# ---------- Input ----------

func _on_slot_input(event: InputEvent, index: int) -> void:
	if not (event is InputEventMouseButton and event.pressed): return
	match event.button_index:
		MOUSE_BUTTON_LEFT:
			# Left click = withdraw 1
			var entry = Bank.slots[index]
			if entry != null:
				Network.send_bank_withdraw(Bank.session_id, int(entry.get("item_id", 0)), 1)
			get_viewport().set_input_as_handled()
		MOUSE_BUTTON_RIGHT:
			_show_withdraw_menu(index)
			get_viewport().set_input_as_handled()


func _show_withdraw_menu(index: int) -> void:
	var entry = Bank.slots[index]
	if entry == null: return
	var item_id := int(entry.get("item_id", 0))
	_context_actions = [
		{"kind": "withdraw", "index": index, "item_id": item_id, "qty": 1,  "label": _text("withdraw_1")},
		{"kind": "withdraw", "index": index, "item_id": item_id, "qty": 10, "label": _text("withdraw_10")},
		{"kind": "withdraw", "index": index, "item_id": item_id, "qty": 100, "label": _text("withdraw_100")},
		{"kind": "withdraw", "index": index, "item_id": item_id, "qty": -2, "label": _text("withdraw_x")},
		{"kind": "withdraw", "index": index, "item_id": item_id, "qty": -1, "label": _text("withdraw_all")},
	]
	var labels: Array = []
	for a in _context_actions: labels.append(str(a["label"]))
	_action_menu.show_actions(labels, get_viewport().get_mouse_position())


func _on_action_menu_selected(index: int) -> void:
	if index < 0 or index >= _context_actions.size(): return
	var act: Dictionary = _context_actions[index]
	var qty := int(act["qty"])
	if qty == -2:
		_open_amount_dialog(int(act["item_id"]), str(act["kind"]))
		return
	_dispatch(act, qty)


func _dispatch(act: Dictionary, qty: int) -> void:
	var item_id := int(act["item_id"])
	match str(act["kind"]):
		"withdraw":
			Network.send_bank_withdraw(Bank.session_id, item_id, qty)


func _open_amount_dialog(item_id: int, kind: String) -> void:
	# Simple inline prompt via ConfirmationDialog; matches the trade panel pattern.
	var dlg := ConfirmationDialog.new()
	dlg.title = "Withdraw amount"
	dlg.ok_button_text = "OK"
	dlg.add_cancel_button("Cancel")
	var le := LineEdit.new()
	le.placeholder_text = "0"
	dlg.add_child(le)
	dlg.confirmed.connect(func():
		if le.text.is_valid_int() and int(le.text) > 0:
			Network.send_bank_withdraw(Bank.session_id, item_id, int(le.text))
		dlg.queue_free())
	dlg.canceled.connect(func(): dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()
	le.grab_focus()


# ---------- Toggle handlers ----------

func _on_cert_toggled(pressed: bool) -> void:
	Bank.cert_mode = pressed
	if Bank.is_active():
		Network.send_bank_cert_mode(Bank.session_id, pressed)


func _on_close_pressed() -> void:
	if Bank.is_active():
		Network.send_bank_close(Bank.session_id)


# ---------- Hover ----------

func _on_slot_hover(entry: Dictionary, _from_bank: bool) -> void:
	var name_text := str(entry.get("display", ""))
	if name_text.is_empty():
		name_text = "Item %d" % int(entry.get("item_id", 0))
	_hover_label.text = UIStyle.format_primary_bbcode("Withdraw " + name_text)
	_hover_panel.visible = true
	_update_hover_position()


func _on_slot_hover_exit() -> void:
	_hover_panel.visible = false


func _update_hover_position() -> void:
	_hover_panel.reset_size()
	var mouse := get_viewport().get_mouse_position()
	var pos := mouse + Vector2(16, -_hover_panel.size.y - 8)
	var vp := get_viewport().get_visible_rect().size
	pos.x = min(pos.x, vp.x - _hover_panel.size.x - 4)
	pos.y = max(pos.y, 4.0)
	_hover_panel.position = Vector2i(pos)


# ---------- Helpers ----------

func _text(key: String, params: Dictionary = {}) -> String:
	return Texts.get_text("game.bank." + key, params)


func _make_toggle_button(text: String) -> Button:
	var btn := Button.new()
	btn.text = text
	btn.toggle_mode = true
	btn.focus_mode = Control.FOCUS_NONE
	btn.add_theme_stylebox_override("normal", _btn_style(Color(0.18, 0.18, 0.18)))
	btn.add_theme_stylebox_override("pressed", _btn_style(Color(0.25, 0.62, 0.25)))
	btn.add_theme_stylebox_override("hover", _btn_style(Color(0.24, 0.24, 0.24)))
	btn.add_theme_stylebox_override("hover_pressed", _btn_style(Color(0.28, 0.68, 0.28)))
	btn.add_theme_color_override("font_color", Color.WHITE)
	btn.add_theme_color_override("font_pressed_color", Color.WHITE)
	btn.add_theme_color_override("font_hover_color", Color.WHITE)
	btn.add_theme_color_override("font_hover_pressed_color", Color.WHITE)
	return btn


func _make_mode_button(text: String) -> Button:
	var btn := Button.new()
	btn.text = text
	btn.focus_mode = Control.FOCUS_NONE
	btn.add_theme_stylebox_override("normal", _btn_style(Color(0.18, 0.18, 0.18)))
	btn.add_theme_stylebox_override("hover", _btn_style(Color(0.24, 0.24, 0.24)))
	btn.add_theme_stylebox_override("pressed", _btn_style(Color(0.18, 0.18, 0.18)))
	btn.add_theme_color_override("font_color", Color.WHITE)
	btn.add_theme_color_override("font_hover_color", Color.WHITE)
	btn.add_theme_color_override("font_pressed_color", Color.WHITE)
	return btn


func _make_action_button(text: String, base: Color) -> Button:
	var btn := Button.new()
	btn.text = text
	btn.add_theme_stylebox_override("normal", _btn_style(base))
	btn.add_theme_stylebox_override("hover", _btn_style(base.lightened(0.12)))
	btn.add_theme_stylebox_override("pressed", _btn_style(base.darkened(0.12)))
	btn.add_theme_color_override("font_color", Color.WHITE)
	btn.add_theme_color_override("font_hover_color", Color.WHITE)
	btn.add_theme_color_override("font_pressed_color", Color.WHITE)
	return btn


func _btn_style(base: Color) -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = base
	sb.border_color = base.darkened(0.25)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = 3
	sb.corner_radius_top_right = 3
	sb.corner_radius_bottom_left = 3
	sb.corner_radius_bottom_right = 3
	sb.content_margin_left = 10
	sb.content_margin_right = 10
	sb.content_margin_top = 4
	sb.content_margin_bottom = 4
	return sb
