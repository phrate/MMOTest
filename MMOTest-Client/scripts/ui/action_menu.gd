class_name ActionMenu
extends Control
## Custom right-click menu. Replaces PopupMenu so per-row verbs can be colored
## blue (PopupMenu doesn't support per-item text coloring). Uses UIStyle so it
## matches the hover infobox exactly.

signal action_selected(index: int)
signal menu_closed

const LEAVE_MARGIN := 40.0

var _panel: PanelContainer
var _vbox: VBoxContainer
var _tracking := false


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	visible = false

	_panel = PanelContainer.new()
	_panel.name = "Panel"
	_panel.mouse_filter = Control.MOUSE_FILTER_STOP
	_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	add_child(_panel)

	_vbox = VBoxContainer.new()
	_vbox.add_theme_constant_override("separation", 2)
	_panel.add_child(_vbox)


func show_actions(items: Array, screen_pos: Vector2) -> void:
	for c in _vbox.get_children():
		c.queue_free()

	for i in items.size():
		_vbox.add_child(_make_action_button(str(items[i]), i))

	_panel.reset_size()

	# Clamp position so the menu doesn't overflow off-screen right/bottom.
	var vp := get_viewport_rect().size
	var desired := screen_pos
	desired.x = clampf(desired.x, 0.0, vp.x - _panel.size.x - 4.0)
	desired.y = clampf(desired.y, 0.0, vp.y - _panel.size.y - 4.0)
	_panel.position = Vector2i(desired)

	visible = true
	_tracking = true


func _hide() -> void:
	visible = false
	_tracking = false
	menu_closed.emit()


func _make_action_button(text: String, index: int) -> Button:
	var btn := Button.new()
	btn.flat = true
	btn.focus_mode = Control.FOCUS_NONE
	btn.custom_minimum_size = Vector2(0, 22)
	btn.add_theme_stylebox_override("normal", UIStyle.make_empty_style())
	btn.add_theme_stylebox_override("hover", UIStyle.make_menu_hover_style())
	btn.add_theme_stylebox_override("pressed", UIStyle.make_menu_hover_style())

	var parts := UIStyle.split_verb(text)
	var verb: String = parts[0]
	var rest: String = parts[1]

	var row := HBoxContainer.new()
	row.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	row.add_theme_constant_override("separation", 0)
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	# Small left padding so text isn't flush against the panel border.
	row.offset_left = 2.0
	row.offset_right = -2.0
	btn.add_child(row)

	row.add_child(_make_label(verb, UIStyle.VERB_COLOR))
	if not rest.is_empty():
		row.add_child(_make_label(" " + rest, UIStyle.TEXT_COLOR))

	btn.pressed.connect(func() -> void: _on_pressed(index))
	return btn


func _make_label(text: String, color: Color) -> Label:
	var lbl := Label.new()
	lbl.text = text
	lbl.add_theme_color_override("font_color", color)
	lbl.add_theme_font_size_override("font_size", UIStyle.FONT_SIZE)
	lbl.add_theme_constant_override("outline_size", UIStyle.OUTLINE_SIZE)
	lbl.add_theme_color_override("font_outline_color", UIStyle.OUTLINE_COLOR)
	lbl.mouse_filter = Control.MOUSE_FILTER_IGNORE
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	return lbl


func _on_pressed(index: int) -> void:
	action_selected.emit(index)
	_hide()


func _process(_delta: float) -> void:
	if not _tracking: return
	if _panel.size.x <= 0 or _panel.size.y <= 0: return
	var mouse := get_viewport().get_mouse_position()
	var rect := Rect2(Vector2(_panel.position), _panel.size)
	if not rect.grow(LEAVE_MARGIN).has_point(mouse):
		_hide()


func _input(event: InputEvent) -> void:
	if not visible: return

	if event is InputEventMouseButton and event.pressed:
		# Clicking inside the panel is handled by the button; only outside
		# clicks close-and-consume.
		if _panel.get_global_rect().has_point(event.position):
			return
		_hide()
		get_viewport().set_input_as_handled()
		return

	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		_hide()
		get_viewport().set_input_as_handled()
