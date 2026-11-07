extends CanvasLayer
## Bottom-centre NPC dialogue. Icon on the left, text and numbered options on the right.
## Server-authoritative: the client only sends dialogue_choose and renders state.

var _overlay: Control
var _panel: PanelContainer
var _text_label: RichTextLabel
var _options_container: VBoxContainer

var _session_id: String = ""


func _ready() -> void:
	layer = 30
	_build_ui()
	Network.dialogue_state_received.connect(_on_state)
	Network.dialogue_closed.connect(_on_closed)
	Network.state_changed.connect(_on_network_state)


func _build_ui() -> void:
	_overlay = Control.new()
	_overlay.name = "DialogueOverlay"
	_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_overlay.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_overlay.visible = false
	_overlay.add_to_group("modal_blockers")
	add_child(_overlay)

	_panel = PanelContainer.new()
	_panel.anchor_left = 0.5
	_panel.anchor_right = 0.5
	_panel.anchor_top = 1.0
	_panel.anchor_bottom = 1.0
	_panel.offset_left = -400.0
	_panel.offset_right = 400.0
	_panel.offset_top = -260.0
	_panel.offset_bottom = -20.0
	_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_overlay.add_child(_panel)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 12)
	margin.add_theme_constant_override("margin_right", 12)
	margin.add_theme_constant_override("margin_top", 10)
	margin.add_theme_constant_override("margin_bottom", 10)
	_panel.add_child(margin)

	var hbox := HBoxContainer.new()
	hbox.add_theme_constant_override("separation", 12)
	margin.add_child(hbox)

	var icon_holder := Control.new()
	icon_holder.custom_minimum_size = Vector2(180, 0)
	icon_holder.size_flags_vertical = Control.SIZE_EXPAND_FILL
	hbox.add_child(icon_holder)

	# Placeholder portrait: a bordered gold square, centered in the icon holder.
	var gold := Panel.new()
	gold.anchor_left = 0.5
	gold.anchor_right = 0.5
	gold.anchor_top = 0.5
	gold.anchor_bottom = 0.5
	gold.offset_left = -90.0
	gold.offset_right = 90.0
	gold.offset_top = -90.0
	gold.offset_bottom = 90.0
	gold.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var gold_style := StyleBoxFlat.new()
	gold_style.bg_color = Color(0.85, 0.68, 0.16)
	gold_style.border_color = Color(0.45, 0.35, 0.08)
	gold_style.border_width_left = 2
	gold_style.border_width_right = 2
	gold_style.border_width_top = 2
	gold_style.border_width_bottom = 2
	gold_style.corner_radius_top_left = 4
	gold_style.corner_radius_top_right = 4
	gold_style.corner_radius_bottom_left = 4
	gold_style.corner_radius_bottom_right = 4
	gold.add_theme_stylebox_override("panel", gold_style)
	icon_holder.add_child(gold)

	var right_col := VBoxContainer.new()
	right_col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right_col.add_theme_constant_override("separation", 12)
	hbox.add_child(right_col)

	_text_label = RichTextLabel.new()
	_text_label.bbcode_enabled = true
	_text_label.fit_content = true
	_text_label.scroll_active = false
	_text_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_text_label.add_theme_font_size_override("normal_font_size", 16)
	_text_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	right_col.add_child(_text_label)

	_options_container = VBoxContainer.new()
	_options_container.add_theme_constant_override("separation", 6)
	right_col.add_child(_options_container)


func _on_state(state: Dictionary) -> void:
	_session_id = str(state.get("session_id", ""))
	_text_label.text = str(state.get("text", ""))

	for c in _options_container.get_children():
		_options_container.remove_child(c)
		c.queue_free()

	var options: Array = state.get("options", [])
	for opt in options:
		if typeof(opt) != TYPE_DICTIONARY: continue
		var index := int(opt.get("index", 0))
		var label := str(opt.get("label", ""))
		var goto_ := str(opt.get("goto", ""))
		if goto_.is_empty(): continue
		var btn := _make_option_button("%d. %s" % [index, label])
		btn.pressed.connect(_on_option_pressed.bind(goto_))
		_options_container.add_child(btn)

	_overlay.visible = true


func _make_option_button(text: String) -> Button:
	var btn := Button.new()
	btn.text = text
	btn.focus_mode = Control.FOCUS_NONE
	btn.alignment = HORIZONTAL_ALIGNMENT_LEFT
	btn.add_theme_color_override("font_color", UIStyle.VERB_COLOR)
	btn.add_theme_color_override("font_hover_color", UIStyle.VERB_COLOR.lightened(0.2))
	btn.add_theme_color_override("font_pressed_color", UIStyle.VERB_COLOR.darkened(0.2))
	btn.add_theme_font_size_override("font_size", 18)
	var empty := StyleBoxEmpty.new()
	btn.add_theme_stylebox_override("normal", empty)
	btn.add_theme_stylebox_override("focus", empty)
	var hover := StyleBoxFlat.new()
	hover.bg_color = Color(1, 1, 1, 0.06)
	hover.content_margin_left = 4
	hover.content_margin_right = 4
	hover.content_margin_top = 4
	hover.content_margin_bottom = 4
	btn.add_theme_stylebox_override("hover", hover)
	btn.add_theme_stylebox_override("pressed", hover)
	return btn


func _on_option_pressed(goto_: String) -> void:
	if _session_id.is_empty(): return
	Network.send_dialogue_choose(_session_id, goto_)


func _on_closed(_session_id: String, error_key: String, error_params: Dictionary) -> void:
	_overlay.visible = false
	_session_id = ""
	for c in _options_container.get_children():
		_options_container.remove_child(c)
		c.queue_free()
	_text_label.text = ""
	if not error_key.is_empty():
		var sub := error_key.trim_prefix("dialogue.")
		var text := Texts.get_text("game.dialogue." + sub, error_params)
		Network.emit_system_message(text)


func _on_network_state(state: int) -> void:
	if state == Network.State.DISCONNECTED:
		_overlay.visible = false
		_session_id = ""
