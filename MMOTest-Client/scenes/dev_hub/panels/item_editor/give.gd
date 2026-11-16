class_name ItemEditorGive
extends VBoxContainer
## "Give to player" sub-section inside the Item Editor's Edit tab. Lets an
## admin hand N of the currently-editing item (or its cert twin) to any
## player by username. Server validates existence + inventory space; this
## panel just collects input and renders the result.
##
## Cert toggle is disabled unless the item can actually be certed:
##   - not stackable (CertGenerator only creates certs for non-stackables)
##   - not already a cert (cert_of_slug empty)
##   - Can Cert flag on (non_certable == false)
## If any of those are violated, certing would either fail on the server or
## silently produce a base item. Disabling the toggle is the honest UX.

signal give_requested(target_username: String, amount: int, is_cert: bool)

var _item_slug: String = ""
var _item_display: String = ""
var _player_field: LineEdit
var _amount_spin: SpinBox
var _cert_toggle: DevToggleButton
var _send_btn: Button
var _message_label: Label

# Snapshotted at send time so the success message reflects what was sent,
# not whatever the fields hold when the response arrives.
var _in_flight_amount: int = 0
var _in_flight_username: String = ""


func _init() -> void:
	add_theme_constant_override("separation", 4)
	_build_ui()


func _build_ui() -> void:
	var s := DevStyle.instance
	var p := "ui.dev_hub.item_editor.give."

	var r_player := HBoxContainer.new()
	r_player.add_theme_constant_override("separation", 6)
	var lbl_p := DevFormFields.compact_label(Texts.get_text(p + "label_player"))
	lbl_p.custom_minimum_size = Vector2(60, 0)
	r_player.add_child(lbl_p)
	_player_field = LineEdit.new()
	_player_field.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_player_field.add_theme_font_size_override("font_size", s.font_size_small)
	r_player.add_child(_player_field)
	add_child(r_player)

	var r_amount := HBoxContainer.new()
	r_amount.add_theme_constant_override("separation", 6)
	var lbl_a := DevFormFields.compact_label(Texts.get_text(p + "label_amount"))
	lbl_a.custom_minimum_size = Vector2(60, 0)
	r_amount.add_child(lbl_a)
	_amount_spin = SpinBox.new()
	_amount_spin.min_value = 1
	_amount_spin.max_value = 999999
	_amount_spin.step = 1
	_amount_spin.value = 1
	_amount_spin.update_on_text_changed = true
	_amount_spin.custom_minimum_size = Vector2(100, 0)
	_amount_spin.add_theme_font_size_override("font_size", s.font_size_small)
	r_amount.add_child(_amount_spin)
	_cert_toggle = DevToggleButton.new()
	_cert_toggle.setup(Texts.get_text(p + "label_cert"), false, DevToggleButton.Kind.GREEN)
	_cert_toggle.tooltip_text = Texts.get_text(p + "cert_tooltip")
	r_amount.add_child(_cert_toggle)
	add_child(r_amount)

	_send_btn = DevFormFields.action_button(
		Texts.get_text(p + "button_send"),
		Color(0.28, 0.62, 0.28), 36, s.font_size_body)
	_send_btn.pressed.connect(_on_send_pressed)
	add_child(_send_btn)

	_message_label = Label.new()
	_message_label.add_theme_font_size_override("font_size", s.font_size_small)
	_message_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	_message_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_message_label.visible = false
	add_child(_message_label)


# ---------- Public API ----------

func set_item_data(src: Dictionary) -> void:
	_item_slug = str(src.get("slug_id", ""))
	_item_display = str(src.get("display", ""))
	var can_cert := (
		not bool(src.get("non_certable", false))
		and not bool(src.get("stackable", false))
		and str(src.get("cert_of_slug", "")).is_empty()
	)
	_cert_toggle.disabled = not can_cert
	if not can_cert:
		_cert_toggle.button_pressed = false
	_clear_message()


func set_current_username(username: String) -> void:
	if _player_field.text.is_empty():
		_player_field.text = username


func show_result(ok: bool, error_key: String, error_params: Dictionary) -> void:
	_send_btn.disabled = false
	if ok:
		var msg := Texts.get_text("ui.dev_hub.item_editor.give.success", {
			"amount": _in_flight_amount,
			"item": _item_display,
			"username": _in_flight_username,
		})
		_set_message(msg, false)
	else:
		_set_message(Texts.get_text(error_key, error_params), true)


# ---------- Internal ----------

func _on_send_pressed() -> void:
	var target := _player_field.text.strip_edges()
	if target.is_empty():
		_set_message(Texts.get_text("ui.dev_hub.item_editor.give.error.player_not_found",
			{"username": ""}), true)
		return
	_clear_message()
	_send_btn.disabled = true
	_in_flight_amount = _read_amount()
	_in_flight_username = target
	give_requested.emit(target, _in_flight_amount, _cert_toggle.button_pressed)

func _read_amount() -> int:
	# SpinBox only commits typed text to `value` on blur/Enter/wheel. Pull the
	# live text from the LineEdit so click-Send without blurring works.
	var le := _amount_spin.get_line_edit()
	var txt := le.text.strip_edges()
	if txt.is_valid_int():
		var v := int(txt)
		v = clampi(v, int(_amount_spin.min_value), int(_amount_spin.max_value))
		if str(v) != txt:
			le.text = str(v)
			le.caret_column = le.text.length()
		_amount_spin.value = v
		return v
	return int(_amount_spin.value)

func _set_message(text: String, is_error: bool) -> void:
	_message_label.text = text
	_message_label.add_theme_color_override("font_color",
		Color(1.0, 0.4, 0.4) if is_error else Color(0.5, 0.9, 0.5))
	_message_label.visible = not text.is_empty()


func _clear_message() -> void:
	_message_label.text = ""
	_message_label.visible = false
