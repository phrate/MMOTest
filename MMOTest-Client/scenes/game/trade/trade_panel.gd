extends CanvasLayer
## Trade window: top-anchored, non-modal. Offer interactions are tick-gated on
## the server. Errors and cancellations are shown in the chat log, not as
## popups — the panel only renders trade state.

const TRADE_PANEL_WIDTH := 512.0
const SLOT_SIZE := Vector2(36, 36)
const GRID_COLS := 4
const SLOT_COUNT := 28

const OUTLINE_COLOR_NONE    := Color(0, 0, 0, 0)
const OUTLINE_COLOR_GREEN   := Color(0.2, 0.9, 0.2, 1.0)
const OUTLINE_COLOR_RED     := Color(0.95, 0.2, 0.2, 1.0)
const OUTLINE_COLOR_RED_DIM := Color(0.95, 0.2, 0.2, 0.3)
const OUTLINE_WIDTH := 2
const FLASH_INTERVAL := 0.4

const SLOT_BG_COLOR     := Color(0.18, 0.18, 0.18, 1.0)
const SLOT_BORDER_COLOR := Color(0.32, 0.32, 0.32, 1.0)
const CREDITS_BG_COLOR     := Color(0.12, 0.10, 0.05, 0.95)
const CREDITS_BORDER_COLOR := Color(0.35, 0.28, 0.10, 1.0)

const BUTTON_GREEN := Color(0.25, 0.62, 0.25)
const BUTTON_RED   := Color(0.62, 0.20, 0.20)

var _overlay: Control
var _panel: PanelContainer
var _content: VBoxContainer

var _invite_panel: PanelContainer
var _invite_label: Label
var _invite_accept_btn: Button
var _invite_decline_btn: Button

var _action_menu_layer: CanvasLayer
var _action_menu: ActionMenu
var _context_actions: Array = []

var _offer_hover_layer: CanvasLayer
var _offer_hover_panel: PanelContainer
var _offer_hover_label: RichTextLabel

# Modal input dialog (Offer X, Withdraw X, credits amount).
var _input_overlay: Control
var _input_title: Label
var _input_prefix: Label
var _input_line: LineEdit
var _input_on_confirm: Callable = Callable()

# Partner-change tracking.
var _their_offer_changed := false
var _their_credits_changed := false
var _prev_their_offer: Array = []
var _prev_their_credits := 0
var _prev_session_id := ""
var _was_my_accepted := false

# Flash pulse.
var _flash_on := false
var _flash_accum := 0.0

# Wrappers, restyled each render.
var _my_grid_wrapper: PanelContainer
var _their_grid_wrapper: PanelContainer
var _their_credits_wrapper: PanelContainer


func _ready() -> void:
	layer = 30
	_build_overlay()
	_build_invite_panel()
	_build_action_menu()
	_build_offer_hover()
	_build_input_dialog()

	Trade.state_changed.connect(_on_state_changed)
	Trade.invite_changed.connect(_on_invite_changed)
	Trade.error_received.connect(_on_error)
	Trade.cancelled.connect(_on_cancelled)
	Trade.completed.connect(_on_completed)
	Trade.offer_amount_requested.connect(_on_external_amount_request)


func _process(delta: float) -> void:
	if _their_offer_changed or _their_credits_changed:
		_flash_accum += delta
		if _flash_accum >= FLASH_INTERVAL:
			_flash_accum = 0.0
			_flash_on = not _flash_on
			_apply_outlines()

	if _offer_hover_panel.visible:
		_update_hover_position()


func _input(event: InputEvent) -> void:
	# Escape closes the input dialog when it's open. The chat box returns early
	# for modal blockers, so this is the only handler that sees the event.
	if _input_overlay != null and _input_overlay.visible:
		if event is InputEventKey and event.pressed and not event.echo \
				and event.keycode == KEY_ESCAPE:
			_cancel_input_dialog()
			get_viewport().set_input_as_handled()


# ============================================================
# Construction
# ============================================================

func _build_overlay() -> void:
	_overlay = Control.new()
	_overlay.name = "TradeOverlay"
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
	_panel.anchor_bottom = 0.0
	_panel.offset_left = 0.0
	_panel.offset_right = 0.0
	_panel.offset_top = 20.0
	_panel.offset_bottom = 20.0
	_panel.grow_horizontal = Control.GROW_DIRECTION_BOTH
	_panel.grow_vertical = Control.GROW_DIRECTION_END
	_panel.custom_minimum_size = Vector2(TRADE_PANEL_WIDTH, 0)
	_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_overlay.add_child(_panel)

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
	var header_spacer := Control.new()
	header_spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	header_spacer.mouse_filter = Control.MOUSE_FILTER_IGNORE
	header.add_child(header_spacer)
	var cancel_button := _make_action_button(_text("cancel_button"), BUTTON_RED)
	cancel_button.pressed.connect(_on_cancel_pressed)
	header.add_child(cancel_button)

	_content = VBoxContainer.new()
	_content.add_theme_constant_override("separation", 8)
	column.add_child(_content)


func _build_invite_panel() -> void:
	var cl := CanvasLayer.new()
	cl.name = "TradeInviteLayer"
	cl.layer = 29
	add_child(cl)

	_invite_panel = PanelContainer.new()
	_invite_panel.anchor_left = 1.0
	_invite_panel.anchor_right = 1.0
	_invite_panel.anchor_top = 0.0
	_invite_panel.anchor_bottom = 0.0
	_invite_panel.offset_left = -300.0
	_invite_panel.offset_right = -16.0
	_invite_panel.offset_top = 16.0
	_invite_panel.offset_bottom = 130.0
	_invite_panel.visible = false
	_invite_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	cl.add_child(_invite_panel)

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 6)
	_invite_panel.add_child(vbox)

	_invite_label = Label.new()
	_invite_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	vbox.add_child(_invite_label)

	var buttons := HBoxContainer.new()
	buttons.add_theme_constant_override("separation", 6)
	vbox.add_child(buttons)

	_invite_accept_btn = _make_action_button(_text("accept_request"), BUTTON_GREEN)
	_invite_accept_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_invite_accept_btn.pressed.connect(_on_invite_accept_pressed)
	buttons.add_child(_invite_accept_btn)

	_invite_decline_btn = _make_action_button(_text("deny"), BUTTON_RED)
	_invite_decline_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_invite_decline_btn.pressed.connect(_on_invite_decline_pressed)
	buttons.add_child(_invite_decline_btn)


func _build_action_menu() -> void:
	_action_menu_layer = CanvasLayer.new()
	_action_menu_layer.name = "TradeActionMenuLayer"
	_action_menu_layer.layer = 31
	add_child(_action_menu_layer)

	_action_menu = ActionMenu.new()
	_action_menu.name = "TradeActionMenu"
	_action_menu_layer.add_child(_action_menu)
	_action_menu.action_selected.connect(_on_action_menu_selected)


func _build_offer_hover() -> void:
	_offer_hover_layer = CanvasLayer.new()
	_offer_hover_layer.name = "TradeOfferHoverLayer"
	_offer_hover_layer.layer = 32
	add_child(_offer_hover_layer)

	_offer_hover_panel = PanelContainer.new()
	_offer_hover_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_offer_hover_panel.visible = false
	_offer_hover_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_offer_hover_layer.add_child(_offer_hover_panel)

	_offer_hover_label = RichTextLabel.new()
	_offer_hover_label.bbcode_enabled = true
	_offer_hover_label.fit_content = true
	_offer_hover_label.scroll_active = false
	_offer_hover_label.autowrap_mode = TextServer.AUTOWRAP_OFF
	_offer_hover_label.custom_minimum_size = Vector2(60, 0)
	_offer_hover_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_offer_hover_label.add_theme_font_size_override("normal_font_size", UIStyle.FONT_SIZE)
	_offer_hover_label.add_theme_constant_override("outline_size", UIStyle.OUTLINE_SIZE)
	_offer_hover_label.add_theme_color_override("font_outline_color", UIStyle.OUTLINE_COLOR)
	_offer_hover_panel.add_child(_offer_hover_label)


func _build_input_dialog() -> void:
	var cl := CanvasLayer.new()
	cl.name = "TradeInputDialogLayer"
	cl.layer = 33
	add_child(cl)

	_input_overlay = Control.new()
	_input_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_input_overlay.mouse_filter = Control.MOUSE_FILTER_STOP
	_input_overlay.visible = false
	_input_overlay.add_to_group("modal_blockers")
	cl.add_child(_input_overlay)

	var shade := ColorRect.new()
	shade.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	shade.color = Color(0, 0, 0, 0.55)
	shade.mouse_filter = Control.MOUSE_FILTER_STOP
	_input_overlay.add_child(shade)

	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_input_overlay.add_child(center)

	var panel := PanelContainer.new()
	panel.custom_minimum_size = Vector2(320, 0)
	panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	center.add_child(panel)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 16)
	margin.add_theme_constant_override("margin_right", 16)
	margin.add_theme_constant_override("margin_top", 14)
	margin.add_theme_constant_override("margin_bottom", 14)
	panel.add_child(margin)

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 12)
	margin.add_child(vbox)

	_input_title = Label.new()
	_input_title.add_theme_font_size_override("font_size", 16)
	vbox.add_child(_input_title)

	var input_row := HBoxContainer.new()
	input_row.add_theme_constant_override("separation", 6)
	vbox.add_child(input_row)

	_input_prefix = Label.new()
	_input_prefix.add_theme_font_size_override("font_size", 18)
	_input_prefix.modulate = Color(1.0, 0.9, 0.5)
	_input_prefix.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	input_row.add_child(_input_prefix)

	_input_line = LineEdit.new()
	_input_line.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_input_line.add_theme_font_size_override("font_size", 18)
	var le_style := StyleBoxFlat.new()
	le_style.bg_color = Color(0.1, 0.1, 0.1, 1.0)
	le_style.border_color = Color(0.3, 0.3, 0.3, 1.0)
	le_style.border_width_bottom = 1
	le_style.content_margin_left = 6
	le_style.content_margin_right = 6
	le_style.content_margin_top = 4
	le_style.content_margin_bottom = 4
	_input_line.add_theme_stylebox_override("normal", le_style)
	var le_focus := le_style.duplicate() as StyleBoxFlat
	le_focus.border_color = UIStyle.VERB_COLOR
	_input_line.add_theme_stylebox_override("focus", le_focus)
	_input_line.text_changed.connect(_on_input_text_changed)
	_input_line.text_submitted.connect(func(_t): _confirm_input_dialog())
	input_row.add_child(_input_line)

	var buttons := HBoxContainer.new()
	buttons.add_theme_constant_override("separation", 8)
	vbox.add_child(buttons)

	var cancel_btn := _make_action_button(_text("cancel_button"), BUTTON_RED)
	cancel_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	cancel_btn.pressed.connect(_cancel_input_dialog)
	buttons.add_child(cancel_btn)

	var ok_btn := _make_action_button("OK", BUTTON_GREEN)
	ok_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	ok_btn.pressed.connect(_confirm_input_dialog)
	buttons.add_child(ok_btn)


# ============================================================
# Signal handlers
# ============================================================

func _on_state_changed() -> void:
	if Trade.session_id.is_empty():
		_prev_their_offer = []
		_prev_their_credits = 0
		_prev_session_id = ""
		_was_my_accepted = false
		_their_offer_changed = false
		_their_credits_changed = false
		_flash_on = false
		_flash_accum = 0.0
		if _input_overlay.visible:
			_cancel_input_dialog()
		_render()
		return

	var was_flashing := _their_offer_changed or _their_credits_changed

	if _prev_session_id != Trade.session_id:
		_prev_session_id = Trade.session_id
		_prev_their_offer = Trade.their_offer.duplicate(true) if Trade.their_offer else []
		_prev_their_credits = Trade.their_credits
		_was_my_accepted = Trade.my_accepted
		_their_offer_changed = false
		_their_credits_changed = false
	else:
		var offer_changed_now := not _offers_equal(Trade.their_offer, _prev_their_offer)
		var credits_changed_now := Trade.their_credits != _prev_their_credits
		if offer_changed_now and _was_my_accepted:
			_their_offer_changed = true
		if credits_changed_now and _was_my_accepted:
			_their_credits_changed = true
		_prev_their_offer = Trade.their_offer.duplicate(true) if Trade.their_offer else []
		_prev_their_credits = Trade.their_credits

	if Trade.my_accepted and not _was_my_accepted:
		_their_offer_changed = false
		_their_credits_changed = false

	_was_my_accepted = Trade.my_accepted

	if not was_flashing and (_their_offer_changed or _their_credits_changed):
		_flash_on = true
		_flash_accum = 0.0
	elif not _their_offer_changed and not _their_credits_changed:
		_flash_on = false
		_flash_accum = 0.0

	_render()


func _on_invite_changed() -> void:
	_render()


func _on_error(error_key: String, error_params: Dictionary) -> void:
	# All trade errors go to chat. No UI popups.
	Network.emit_system_message(_error_text(error_key, error_params))


func _on_cancelled(error_key: String, error_params: Dictionary) -> void:
	# Trade ended. Reason goes to chat. Trade.state_changed already cleared state.
	Network.emit_system_message(_error_text(error_key, error_params))


func _on_completed() -> void:
	_render()


func _on_external_amount_request(item_id: int) -> void:
	if not Trade.is_active() or Trade.phase != "offering": return
	_open_offer_x(item_id)


# ============================================================
# Render
# ============================================================

func _render() -> void:
	for child in _content.get_children():
		_content.remove_child(child)
		child.queue_free()
	_my_grid_wrapper = null
	_their_grid_wrapper = null
	_their_credits_wrapper = null

	if Trade.has_invite():
		_overlay.visible = false
		_render_invite_window()
		return

	_invite_panel.visible = false

	if Trade.is_active():
		_overlay.visible = true
		_render_trade()
		return

	_overlay.visible = false


# ============================================================
# Incoming invite
# ============================================================

func _render_invite_window() -> void:
	_invite_label.text = _text("incoming", {"username": str(Trade.pending_invite.get("from_username", ""))})
	_invite_accept_btn.disabled = Trade.invite_pending
	_invite_decline_btn.disabled = Trade.invite_pending
	_invite_accept_btn.text = _text("waiting") if Trade.invite_pending else _text("accept_request")
	_invite_panel.visible = true


func _on_invite_accept_pressed() -> void:
	if Trade.invite_pending: return
	Trade.invite_pending = true
	Network.respond_to_trade_invite(str(Trade.pending_invite.get("invite_id", "")), true)
	_render_invite_window()


func _on_invite_decline_pressed() -> void:
	Network.respond_to_trade_invite(str(Trade.pending_invite.get("invite_id", "")), false)
	Trade.pending_invite = {}
	Trade.invite_pending = false
	Trade.invite_changed.emit()


# ============================================================
# Trade panel
# ============================================================

func _render_trade() -> void:
	var is_confirm := Trade.phase == "confirming"
	_panel.custom_minimum_size = Vector2(TRADE_PANEL_WIDTH, 0)

	var columns := HBoxContainer.new()
	columns.add_theme_constant_override("separation", 12)
	_content.add_child(columns)

	var my_col := VBoxContainer.new()
	my_col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	my_col.add_theme_constant_override("separation", 4)
	columns.add_child(my_col)
	my_col.add_child(_make_label(_text("your_offer")))
	my_col.add_child(_make_credits_bar(Trade.my_credits, not is_confirm))
	_my_grid_wrapper = _wrap_with_outline(_build_offer_grid(Trade.my_offer, not is_confirm))
	my_col.add_child(_my_grid_wrapper)

	var their_col := VBoxContainer.new()
	their_col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	their_col.add_theme_constant_override("separation", 4)
	columns.add_child(their_col)
	their_col.add_child(_make_their_offer_label())
	_their_credits_wrapper = _wrap_with_outline(
		_make_credits_bar(Trade.their_credits, false), 2, Control.SIZE_FILL)
	their_col.add_child(_their_credits_wrapper)
	_their_grid_wrapper = _wrap_with_outline(_build_offer_grid(Trade.their_offer, false))
	their_col.add_child(_their_grid_wrapper)

	var bottom_text := _text("confirm") if is_confirm else _text("accept_offer")
	var bottom := _make_action_button(bottom_text, BUTTON_GREEN, Color(0.45, 0.45, 0.45))
	bottom.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	if is_confirm:
		bottom.disabled = Trade.my_confirmed
		bottom.pressed.connect(_on_confirm_pressed)
	else:
		bottom.disabled = Trade.my_accepted
		bottom.pressed.connect(_on_accept_offer_pressed)
	_content.add_child(bottom)

	_apply_outlines()


func _wrap_with_outline(inner: Control, pad: int = 4,
		h_flags: int = Control.SIZE_SHRINK_CENTER) -> PanelContainer:
	var wrapper := PanelContainer.new()
	wrapper.mouse_filter = Control.MOUSE_FILTER_IGNORE
	wrapper.size_flags_horizontal = h_flags
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0, 0, 0, 0)
	style.border_color = OUTLINE_COLOR_NONE
	style.border_width_left = OUTLINE_WIDTH
	style.border_width_right = OUTLINE_WIDTH
	style.border_width_top = OUTLINE_WIDTH
	style.border_width_bottom = OUTLINE_WIDTH
	style.content_margin_left = pad
	style.content_margin_right = pad
	style.content_margin_top = pad
	style.content_margin_bottom = pad
	wrapper.add_theme_stylebox_override("panel", style)
	wrapper.add_child(inner)
	return wrapper


func _apply_outlines() -> void:
	if _my_grid_wrapper != null and is_instance_valid(_my_grid_wrapper):
		var color := OUTLINE_COLOR_GREEN if Trade.my_accepted else OUTLINE_COLOR_NONE
		_set_outline(_my_grid_wrapper, color)
	if _their_grid_wrapper != null and is_instance_valid(_their_grid_wrapper):
		var color := OUTLINE_COLOR_NONE
		if _their_offer_changed:
			color = OUTLINE_COLOR_RED if _flash_on else OUTLINE_COLOR_RED_DIM
		elif Trade.their_accepted:
			color = OUTLINE_COLOR_GREEN
		_set_outline(_their_grid_wrapper, color)
	if _their_credits_wrapper != null and is_instance_valid(_their_credits_wrapper):
		var color := OUTLINE_COLOR_NONE
		if _their_credits_changed:
			color = OUTLINE_COLOR_RED if _flash_on else OUTLINE_COLOR_RED_DIM
		_set_outline(_their_credits_wrapper, color)


func _set_outline(wrapper: PanelContainer, color: Color) -> void:
	var style := wrapper.get_theme_stylebox("panel") as StyleBoxFlat
	if style == null or style.border_color == color:
		return
	style.border_color = color
	wrapper.add_theme_stylebox_override("panel", style)
	wrapper.queue_redraw()


# ============================================================
# Offer grids
# ============================================================

func _build_offer_grid(offer: Array, allow_withdraw: bool) -> Control:
	var grid := GridContainer.new()
	grid.columns = GRID_COLS
	grid.add_theme_constant_override("h_separation", 2)
	grid.add_theme_constant_override("v_separation", 2)

	var slots := _expand_offer_slots(offer)
	for i in SLOT_COUNT:
		var btn := _make_slot_button()
		if i < slots.size():
			var entry: Dictionary = slots[i]
			_render_slot_contents(btn, entry)
			btn.mouse_entered.connect(_on_offer_slot_hover_enter.bind(entry, allow_withdraw))
			btn.mouse_exited.connect(_on_offer_slot_hover_exit)
			if allow_withdraw:
				btn.gui_input.connect(_on_offer_slot_input.bind(entry))
		grid.add_child(btn)
	return grid


func _make_slot_button() -> Button:
	var btn := Button.new()
	btn.custom_minimum_size = SLOT_SIZE
	btn.focus_mode = Control.FOCUS_NONE
	btn.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
	btn.vertical_icon_alignment = VERTICAL_ALIGNMENT_CENTER
	btn.alignment = HORIZONTAL_ALIGNMENT_CENTER
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
	btn.add_theme_stylebox_override("disabled", style)
	btn.add_theme_stylebox_override("focus", StyleBoxEmpty.new())
	return btn


func _expand_offer_slots(offer: Array) -> Array:
	var out: Array = []
	for entry in offer:
		var qty := int(entry.get("quantity", 0))
		if qty <= 0: continue
		var stackable := bool(entry.get("stackable", false))
		if stackable:
			out.append(entry)
		else:
			for _i in qty:
				out.append(entry.duplicate(true).merged({"quantity": 1, "stackable": false}, true))
	return out


func _render_slot_contents(btn: Button, entry: Dictionary) -> void:
	var display := str(entry.get("display", entry.get("slug", "?")))
	var icon_path := "res://resources/items/icons/pink_cube_icon.png"
	if not str(entry.get("cert_of_slug", "")).is_empty():
		icon_path = "res://resources/items/icons/brown_cube_icon.png"
	if ResourceLoader.exists(icon_path):
		btn.icon = load(icon_path)
		btn.expand_icon = false
		btn.add_theme_constant_override("icon_max_width", 28)
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


# ============================================================
# Offer hover
# ============================================================

func _on_offer_slot_hover_enter(entry: Dictionary, allow_withdraw: bool) -> void:
	var name_text := str(entry.get("display", ""))
	var qty := int(entry.get("quantity", 1))
	var item_text := name_text if qty <= 1 else "%s x%d" % [name_text, qty]
	if allow_withdraw:
		_offer_hover_label.text = UIStyle.format_primary_bbcode("Withdraw " + item_text)
	else:
		_offer_hover_label.text = item_text
	_offer_hover_panel.visible = true
	_update_hover_position()


func _on_offer_slot_hover_exit() -> void:
	_offer_hover_panel.visible = false


func _update_hover_position() -> void:
	_offer_hover_panel.reset_size()
	var mouse := get_viewport().get_mouse_position()
	var pos := mouse + Vector2(16, -_offer_hover_panel.size.y - 8)
	var vp := get_viewport().get_visible_rect().size
	pos.x = min(pos.x, vp.x - _offer_hover_panel.size.x - 4)
	pos.y = max(pos.y, 4.0)
	_offer_hover_panel.position = Vector2i(pos)


# ============================================================
# Offer slot input
# ============================================================

func _on_offer_slot_input(event: InputEvent, entry: Dictionary) -> void:
	if not (event is InputEventMouseButton and event.pressed): return
	var item_id := int(entry.get("item_id", 0))
	if item_id == 0: return
	match event.button_index:
		MOUSE_BUTTON_LEFT:
			Network.send_trade_offer_withdraw(Trade.session_id, item_id, 1)
			get_viewport().set_input_as_handled()
		MOUSE_BUTTON_RIGHT:
			_show_withdraw_menu(item_id, get_viewport().get_mouse_position())
			get_viewport().set_input_as_handled()


func _show_withdraw_menu(item_id: int, pos: Vector2) -> void:
	_context_actions = [
		{"kind": "withdraw", "item_id": item_id, "qty": 1,   "label": _text("withdraw_1")},
		{"kind": "withdraw", "item_id": item_id, "qty": 10,  "label": _text("withdraw_10")},
		{"kind": "withdraw", "item_id": item_id, "qty": 100, "label": _text("withdraw_100")},
		{"kind": "withdraw", "item_id": item_id, "qty": -2,  "label": _text("withdraw_x")},
		{"kind": "withdraw", "item_id": item_id, "qty": -1,  "label": _text("withdraw_all")},
	]
	var displays: Array = []
	for act in _context_actions:
		displays.append(str(act.get("label", "")))
	_action_menu.show_actions(displays, pos)


func _on_action_menu_selected(index: int) -> void:
	if index < 0 or index >= _context_actions.size(): return
	var act: Dictionary = _context_actions[index]
	var qty := int(act.get("qty", 0))
	if qty == -2:
		if str(act.get("kind", "")) == "offer":
			_open_offer_x(int(act.get("item_id", 0)))
		else:
			_open_withdraw_x(int(act.get("item_id", 0)))
		return
	_dispatch_offer_action(act, qty)


func _dispatch_offer_action(act: Dictionary, qty: int) -> void:
	var item_id := int(act.get("item_id", 0))
	match str(act.get("kind", "")):
		"offer":
			Network.send_trade_offer_add(Trade.session_id, item_id, qty)
		"withdraw":
			Network.send_trade_offer_withdraw(Trade.session_id, item_id, qty)


# ============================================================
# Input dialog
# ============================================================

func _open_offer_x(item_id: int) -> void:
	_open_input_dialog(
		"Offer amount", "", "",
		func(v): Network.send_trade_offer_add(Trade.session_id, item_id, v))


func _open_withdraw_x(item_id: int) -> void:
	_open_input_dialog(
		"Withdraw amount", "", "",
		func(v): Network.send_trade_offer_withdraw(Trade.session_id, item_id, v))


func _open_credits_dialog() -> void:
	_open_input_dialog(
		_text("credits_dialog_title"), "Ȼ", "",
		func(v): Network.send_trade_credits(Trade.session_id, v))


func _open_input_dialog(title: String, prefix: String, initial: String, callback: Callable) -> void:
	_input_title.text = title
	_input_prefix.text = prefix
	_input_prefix.visible = not prefix.is_empty()
	_input_line.text = initial
	_input_on_confirm = callback
	_input_overlay.visible = true
	_input_line.grab_focus()
	_input_line.select_all()


func _cancel_input_dialog() -> void:
	_input_overlay.visible = false
	_input_line.release_focus()
	_input_on_confirm = Callable()


func _confirm_input_dialog() -> void:
	var text := _input_line.text.strip_edges()
	_input_overlay.visible = false
	_input_line.release_focus()
	if text.is_empty() or not text.is_valid_int():
		_input_on_confirm = Callable()
		return
	var value := int(text)
	if value < 0:
		_input_on_confirm = Callable()
		return
	var cb := _input_on_confirm
	_input_on_confirm = Callable()
	if cb.is_valid():
		cb.call(value)


func _on_input_text_changed(_t: String) -> void:
	var digits := ""
	for c in _input_line.text:
		if c >= "0" and c <= "9": digits += c
	if _input_line.text != digits:
		var caret := _input_line.caret_column
		_input_line.text = digits
		_input_line.caret_column = mini(caret, digits.length())


# ============================================================
# Credits bar
# ============================================================

func _make_credits_bar(amount: int, clickable: bool) -> Control:
	var panel := PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = CREDITS_BG_COLOR
	style.border_color = CREDITS_BORDER_COLOR
	style.border_width_left = 1
	style.border_width_right = 1
	style.border_width_top = 1
	style.border_width_bottom = 1
	style.content_margin_left = 6
	style.content_margin_right = 6
	style.content_margin_top = 3
	style.content_margin_bottom = 3
	panel.add_theme_stylebox_override("panel", style)

	var lbl := Label.new()
	lbl.text = "Ȼ: %s" % NumberText.with_commas(amount)
	lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	lbl.add_theme_font_size_override("font_size", 14)
	lbl.modulate = Color(1.0, 0.9, 0.5)
	lbl.mouse_filter = Control.MOUSE_FILTER_IGNORE
	panel.add_child(lbl)

	if clickable:
		panel.mouse_filter = Control.MOUSE_FILTER_STOP
		panel.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
		panel.gui_input.connect(_on_credits_bar_input)
	else:
		panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return panel


func _on_credits_bar_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		_open_credits_dialog()
		get_viewport().set_input_as_handled()


# ============================================================
# Button handlers
# ============================================================

func _on_accept_offer_pressed() -> void:
	Network.accept_trade_offer(Trade.session_id)


func _on_confirm_pressed() -> void:
	Network.confirm_trade(Trade.session_id)


func _on_cancel_pressed() -> void:
	if Trade.is_active():
		Network.cancel_trade(Trade.session_id)
	elif Trade.has_invite():
		_on_invite_decline_pressed()


# ============================================================
# Helpers
# ============================================================

func _make_action_button(text: String, base: Color,
		disabled_base: Color = Color(0, 0, 0, 0)) -> Button:
	var btn := Button.new()
	btn.text = text
	var dis_color := disabled_base if disabled_base.a > 0.0 else base.darkened(0.4)
	btn.add_theme_stylebox_override("normal", _action_style(base))
	btn.add_theme_stylebox_override("hover", _action_style(base.lightened(0.12)))
	btn.add_theme_stylebox_override("pressed", _action_style(base.darkened(0.12)))
	btn.add_theme_stylebox_override("disabled", _action_style(dis_color))
	btn.add_theme_stylebox_override("focus", StyleBoxEmpty.new())
	btn.add_theme_color_override("font_color", Color.WHITE)
	btn.add_theme_color_override("font_hover_color", Color.WHITE)
	btn.add_theme_color_override("font_pressed_color", Color.WHITE)
	btn.add_theme_color_override("font_disabled_color", Color(0.8, 0.8, 0.8, 1.0))
	btn.add_theme_constant_override("outline_size", 1)
	btn.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.55))
	return btn


func _action_style(base: Color) -> StyleBoxFlat:
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
	sb.content_margin_left = 12
	sb.content_margin_right = 12
	sb.content_margin_top = 4
	sb.content_margin_bottom = 4
	return sb


func _offers_equal(a: Array, b: Array) -> bool:
	if a.size() != b.size(): return false
	var a_map := {}
	for entry in a:
		a_map[int(entry.get("item_id", 0))] = int(entry.get("quantity", 0))
	for entry in b:
		var iid := int(entry.get("item_id", 0))
		if a_map.get(iid, -1) != int(entry.get("quantity", 0)):
			return false
	return true


func _text(key: String, params: Dictionary = {}) -> String:
	return Texts.get_text("game.trade." + key, params)


func _error_text(error_key: String, params: Dictionary) -> String:
	var sub := error_key.trim_prefix("trade.")
	return _text(sub, params)


func _make_label(text: String) -> Label:
	var lbl := Label.new()
	lbl.text = text
	lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	return lbl


func _make_plain_label(text: String) -> Label:
	var lbl := Label.new()
	lbl.text = text
	return lbl


func _make_their_offer_label() -> Control:
	var hbox := HBoxContainer.new()
	hbox.add_theme_constant_override("separation", 0)
	var name_lbl := _make_plain_label(Trade.partner_username)
	name_lbl.add_theme_color_override("font_color", UIStyle.VERB_COLOR)
	hbox.add_child(name_lbl)
	hbox.add_child(_make_plain_label("'s offer"))
	return hbox
