class_name DevTagInput
extends VBoxContainer
## Chip list plus an input field with suggestion popup. Add tags by typing +
## Enter (or picking a suggestion); remove via ×. The input retains focus
## after each submission (release+grab technique, which is the only reliable
## way to re-enter editing state on Godot 4.4).
##
## The suggestion popup is an inline Control-based panel, NOT a PopupMenu.
## PopupMenu extends Window and can steal keyboard focus on popup() (even
## with FLAG_NO_FOCUS set on some Godot versions), which interrupts typing.
## A PanelContainer with top_level=true escapes container layout and floats
## over the form without ever touching input focus.
##
## The LineEdit is named _input_field (not _input) because `_input` is a
## reserved Godot virtual method for catching raw input events.

signal tags_changed(tags: Array)

const CHIP_SEP := 4
const INPUT_WIDTH := 260
const MAX_SUGGESTIONS := 8

var _tags: Array = []
var _suggestions: Array = []
var _chips_box: HFlowContainer
var _input_field: LineEdit
var _popup_panel: PanelContainer
var _popup_vbox: VBoxContainer
var _popup_tags: Array = []


func _init() -> void:
	add_theme_constant_override("separation", 6)
	_chips_box = HFlowContainer.new()
	_chips_box.add_theme_constant_override("h_separation", CHIP_SEP)
	_chips_box.add_theme_constant_override("v_separation", CHIP_SEP)
	add_child(_chips_box)
	_input_field = LineEdit.new()
	_input_field.custom_minimum_size = Vector2(INPUT_WIDTH, 0)
	_input_field.text_submitted.connect(_on_submit)
	_input_field.text_changed.connect(_on_text_changed)
	add_child(_input_field)
	_build_popup()


func set_placeholder(text: String) -> void:
	_input_field.placeholder_text = text


func set_tags(tags: Array) -> void:
	_tags = tags.duplicate()
	_rebuild()
	_hide_popup()


func get_tags() -> Array:
	return _tags.duplicate()


func set_suggestions(tags: Array) -> void:
	_suggestions = tags.duplicate()


# ---------- Popup construction ----------

func _build_popup() -> void:
	_popup_panel = PanelContainer.new()
	_popup_panel.top_level = true
	_popup_panel.visible = false
	_popup_panel.mouse_filter = Control.MOUSE_FILTER_STOP
	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.10, 0.10, 0.14, 0.98)
	sb.border_color = Color(0.35, 0.35, 0.42, 1.0)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = 3
	sb.corner_radius_top_right = 3
	sb.corner_radius_bottom_left = 3
	sb.corner_radius_bottom_right = 3
	sb.content_margin_left = 4
	sb.content_margin_right = 4
	sb.content_margin_top = 4
	sb.content_margin_bottom = 4
	_popup_panel.add_theme_stylebox_override("panel", sb)
	add_child(_popup_panel)

	_popup_vbox = VBoxContainer.new()
	_popup_vbox.add_theme_constant_override("separation", 2)
	_popup_panel.add_child(_popup_vbox)


func _show_popup() -> void:
	_popup_panel.reset_size()
	_popup_panel.global_position = Vector2(
		_input_field.global_position.x,
		_input_field.global_position.y + _input_field.size.y + 2)
	_popup_panel.visible = true


func _hide_popup() -> void:
	_popup_panel.visible = false


func _clear_popup_buttons() -> void:
	for c in _popup_vbox.get_children():
		_popup_vbox.remove_child(c)
		c.queue_free()
	_popup_tags.clear()


func _rebuild_popup(filter: String) -> void:
	_clear_popup_buttons()
	var s := DevStyle.instance
	var count := 0
	for tag_v in _suggestions:
		var tag := str(tag_v)
		if _tags.has(tag): continue
		if not tag.to_lower().begins_with(filter): continue
		var btn := Button.new()
		btn.text = tag
		btn.alignment = HORIZONTAL_ALIGNMENT_LEFT
		btn.focus_mode = Control.FOCUS_NONE
		btn.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
		btn.add_theme_font_size_override("font_size", s.font_size_body)
		btn.add_theme_color_override("font_color", Color.WHITE)
		btn.add_theme_color_override("font_hover_color", Color.WHITE)
		btn.add_theme_stylebox_override("normal", StyleBoxEmpty.new())
		btn.add_theme_stylebox_override("pressed", StyleBoxEmpty.new())
		var hover := StyleBoxFlat.new()
		hover.bg_color = Color(1, 1, 1, 0.12)
		hover.content_margin_left = 6
		hover.content_margin_right = 6
		hover.content_margin_top = 2
		hover.content_margin_bottom = 2
		btn.add_theme_stylebox_override("hover", hover)
		btn.pressed.connect(_on_suggestion_pressed.bind(tag))
		_popup_vbox.add_child(btn)
		_popup_tags.append(tag)
		count += 1
		if count >= MAX_SUGGESTIONS: break


# ---------- Global input (close on outside click / Esc) ----------

func _input(event: InputEvent) -> void:
	if not _popup_panel.visible: return
	if not is_visible_in_tree(): return
	if event is InputEventMouseButton and event.pressed:
		if not _popup_panel.get_global_rect().has_point(event.position):
			_hide_popup()
	elif event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_ESCAPE:
			_hide_popup()
			get_viewport().set_input_as_handled()


# ---------- Tag list ----------

func _rebuild() -> void:
	for c in _chips_box.get_children():
		_chips_box.remove_child(c)
		c.queue_free()
	for tag in _tags:
		_chips_box.add_child(_make_chip(str(tag)))


func _on_submit(text: String) -> void:
	var tag := text.strip_edges()
	if not tag.is_empty() and not _tags.has(tag):
		_append_tag(tag)
	_input_field.clear()
	_input_field.release_focus()
	_input_field.grab_focus()
	_hide_popup()


func _on_text_changed(t: String) -> void:
	var filter := t.strip_edges().to_lower()
	if filter.is_empty():
		_hide_popup()
		_clear_popup_buttons()
		return
	_rebuild_popup(filter)
	if _popup_tags.size() > 0:
		_show_popup()
	else:
		_hide_popup()


func _on_suggestion_pressed(tag: String) -> void:
	if not _tags.has(tag):
		_append_tag(tag)
	_input_field.clear()
	_input_field.release_focus()
	_input_field.grab_focus()
	_hide_popup()


func _append_tag(tag: String) -> void:
	_tags.append(tag)
	_chips_box.add_child(_make_chip(tag))
	tags_changed.emit(_tags.duplicate())


func _make_chip(tag: String) -> PanelContainer:
	var s := DevStyle.instance
	var chip := PanelContainer.new()
	chip.set_meta("tag", tag)
	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.20, 0.30, 0.45, 1.0)
	sb.border_color = Color(0.40, 0.55, 0.75, 1.0)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = 4
	sb.corner_radius_top_right = 4
	sb.corner_radius_bottom_left = 4
	sb.corner_radius_bottom_right = 4
	sb.content_margin_left = 12
	sb.content_margin_right = 6
	sb.content_margin_top = 6
	sb.content_margin_bottom = 6
	chip.add_theme_stylebox_override("panel", sb)
	var hbox := HBoxContainer.new()
	hbox.add_theme_constant_override("separation", 8)
	chip.add_child(hbox)
	var lbl := Label.new()
	lbl.text = tag
	lbl.add_theme_font_size_override("font_size", s.font_size_body)
	lbl.add_theme_color_override("font_color", Color.WHITE)
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	hbox.add_child(lbl)
	var x := Button.new()
	x.text = "×"
	x.flat = true
	x.focus_mode = Control.FOCUS_NONE
	x.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	x.add_theme_font_size_override("font_size", s.font_size_body)
	x.add_theme_color_override("font_color", Color.WHITE)
	x.pressed.connect(func():
		_tags.erase(tag)
		chip.queue_free()
		tags_changed.emit(_tags.duplicate())
	)
	hbox.add_child(x)
	return chip
