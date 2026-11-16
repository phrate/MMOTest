class_name WorldEditorToolbar
extends HBoxContainer
## Top row of the world editor. Owns mode buttons, Z spinbox, walkable toggle,
## brush toggle + typeahead popup, and Save button. Emits state-change signals
## that the panel forwards to the canvas and to its own tile-bookkeeping.

signal mode_changed(mode: int)
signal z_changed(z: int)
signal walkable_changed(walkable: bool)
signal brush_chosen(type_id: int)
signal brush_cleared()
signal save_pressed()

const SAVE_BTN_WIDTH := 200
const SAVE_BTN_HEIGHT := 48
const BRUSH_MAX_SUGGESTIONS := 8

var _mode := WorldEditorCanvas.MODE_INSPECT
var _mode_btn_group: Array = []
var _z_spin: SpinBox
var _walkable_toggle: Button
var _brush_toggle: Button
var _brush_input: LineEdit
var _brush_popup: PopupMenu
var _save_button: Button
var _type_options: Array = []


func _init() -> void:
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)
	_build_ui()


func _build_ui() -> void:
	var s := DevStyle.instance

	_mode_btn_group.append(_make_mode_button(
		Texts.get_text("ui.dev_hub.world_editor.mode_inspect"), WorldEditorCanvas.MODE_INSPECT))
	_mode_btn_group.append(_make_mode_button(
		Texts.get_text("ui.dev_hub.world_editor.mode_paint"), WorldEditorCanvas.MODE_PAINT))
	_mode_btn_group.append(_make_mode_button(
		Texts.get_text("ui.dev_hub.world_editor.mode_erase"), WorldEditorCanvas.MODE_ERASE))
	for b in _mode_btn_group:
		add_child(b)
	_sync_mode_buttons()

	add_child(VSeparator.new())

	var z_lbl := Label.new()
	z_lbl.text = Texts.get_text("ui.dev_hub.world_editor.label_z")
	z_lbl.add_theme_font_size_override("font_size", s.font_size_small)
	z_lbl.add_theme_color_override("font_color", s.text_muted)
	z_lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	add_child(z_lbl)

	_z_spin = SpinBox.new()
	_z_spin.min_value = 0
	_z_spin.max_value = 999
	_z_spin.step = 1
	_z_spin.value = 100
	_z_spin.custom_minimum_size = Vector2(80, 0)
	_z_spin.value_changed.connect(func(v: float) -> void: z_changed.emit(int(v)))
	add_child(_z_spin)

	_walkable_toggle = Button.new()
	_walkable_toggle.toggle_mode = true
	_walkable_toggle.button_pressed = true
	_walkable_toggle.focus_mode = Control.FOCUS_NONE
	_walkable_toggle.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	_walkable_toggle.text = "[x] " + Texts.get_text("ui.dev_hub.world_editor.label_walkable")
	_walkable_toggle.add_theme_font_size_override("font_size", s.font_size_small)
	_walkable_toggle.add_theme_color_override("font_color", s.text_primary)
	_walkable_toggle.toggled.connect(_on_walkable_toggled)
	add_child(_walkable_toggle)

	add_child(VSeparator.new())

	_brush_toggle = Button.new()
	_brush_toggle.toggle_mode = true
	_brush_toggle.button_pressed = false
	_brush_toggle.focus_mode = Control.FOCUS_NONE
	_brush_toggle.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	_brush_toggle.text = Texts.get_text("ui.dev_hub.world_editor.brush_toggle")
	_brush_toggle.add_theme_font_size_override("font_size", s.font_size_small)
	_brush_toggle.add_theme_color_override("font_color", s.text_primary)
	_brush_toggle.toggled.connect(_on_brush_toggled)
	add_child(_brush_toggle)

	_brush_input = LineEdit.new()
	_brush_input.placeholder_text = Texts.get_text("ui.dev_hub.world_editor.brush_search_placeholder")
	_brush_input.custom_minimum_size = Vector2(200, 0)
	_brush_input.text_changed.connect(_on_brush_filter_changed)
	_brush_input.visible = false
	add_child(_brush_input)

	_brush_popup = PopupMenu.new()
	_brush_popup.add_theme_font_size_override("font_size", s.font_size_small)
	_brush_popup.id_pressed.connect(_on_brush_suggestion_chosen)
	add_child(_brush_popup)

	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add_child(spacer)

	_save_button = Button.new()
	_save_button.text = Texts.get_text("ui.dev_hub.world_editor.save_button")
	_save_button.focus_mode = Control.FOCUS_NONE
	_save_button.custom_minimum_size = Vector2(SAVE_BTN_WIDTH, SAVE_BTN_HEIGHT)
	_save_button.add_theme_font_size_override("font_size", 20)
	_save_button.add_theme_color_override("font_color", Color(1, 1, 1))
	_save_button.add_theme_color_override("font_hover_color", Color(1, 1, 1))
	_save_button.add_theme_color_override("font_pressed_color", Color(1, 1, 1))
	_save_button.add_theme_color_override("font_disabled_color", Color(0.75, 0.75, 0.75))
	var green := Color(0.28, 0.62, 0.28)
	_save_button.add_theme_stylebox_override("normal", s.make_button_style(green))
	_save_button.add_theme_stylebox_override("hover", s.make_button_style(green.lightened(0.12)))
	_save_button.add_theme_stylebox_override("pressed", s.make_button_style(green.darkened(0.12)))
	_save_button.add_theme_stylebox_override("disabled", s.make_button_style(green.darkened(0.35)))
	_save_button.pressed.connect(func() -> void: save_pressed.emit())
	add_child(_save_button)


# ---------- Public API ----------

func set_type_options(options: Array) -> void:
	_type_options = options

func set_save_state(text: String, disabled: bool) -> void:
	if _save_button == null: return
	_save_button.text = text
	_save_button.disabled = disabled


# ---------- Mode buttons ----------

func _make_mode_button(text: String, mode: int) -> Button:
	var s := DevStyle.instance
	var b := Button.new()
	b.text = text
	b.toggle_mode = true
	b.focus_mode = Control.FOCUS_NONE
	b.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	b.add_theme_font_size_override("font_size", s.font_size_small)
	b.add_theme_color_override("font_color", s.text_primary)
	b.add_theme_color_override("font_hover_color", s.text_primary)
	b.add_theme_color_override("font_pressed_color", s.text_heading)
	b.pressed.connect(_on_mode_button_pressed.bind(mode))
	return b

func _on_mode_button_pressed(mode: int) -> void:
	if _brush_toggle.button_pressed:
		_brush_toggle.button_pressed = false
		_brush_input.visible = false
		_brush_popup.hide()
		brush_cleared.emit()
	_mode = mode
	_sync_mode_buttons()
	mode_changed.emit(mode)

func _sync_mode_buttons() -> void:
	for i in range(_mode_btn_group.size()):
		var b: Button = _mode_btn_group[i]
		b.set_pressed_no_signal(i == _mode)


# ---------- Z / Walkable ----------

func _on_walkable_toggled(pressed: bool) -> void:
	_walkable_toggle.text = ("[x] " if pressed else "[ ] ") + Texts.get_text("ui.dev_hub.world_editor.label_walkable")
	walkable_changed.emit(pressed)


# ---------- Brush ----------

func _on_brush_toggled(pressed: bool) -> void:
	_brush_input.visible = pressed
	if pressed:
		_mode = WorldEditorCanvas.MODE_BRUSH
		_sync_mode_buttons()
		_brush_input.grab_focus()
		mode_changed.emit(WorldEditorCanvas.MODE_BRUSH)
	else:
		_mode = WorldEditorCanvas.MODE_INSPECT
		_sync_mode_buttons()
		_brush_input.text = ""
		_brush_popup.hide()
		brush_cleared.emit()
		mode_changed.emit(WorldEditorCanvas.MODE_INSPECT)

func _on_brush_filter_changed(t: String) -> void:
	var filter := t.strip_edges().to_lower()
	if filter.is_empty():
		_brush_popup.hide()
		return
	_brush_popup.clear()
	var count := 0
	for it in _type_options:
		if typeof(it) != TYPE_DICTIONARY: continue
		var slug := str(it.get("slug_id", ""))
		var disp := str(it.get("display", ""))
		if not (slug.to_lower().contains(filter) or disp.to_lower().contains(filter)):
			continue
		if count >= BRUSH_MAX_SUGGESTIONS: break
		_brush_popup.add_item("%s  (%s)" % [disp, slug], count)
		_brush_popup.set_item_metadata(count, int(it.get("id", 0)))
		count += 1
	if count > 0:
		var rect := _brush_input.get_global_rect()
		_brush_popup.position = Vector2i(int(rect.position.x), int(rect.position.y + rect.size.y + 2))
		_brush_popup.reset_size()
		_brush_popup.popup()
	else:
		_brush_popup.hide()

func _on_brush_suggestion_chosen(id: int) -> void:
	var type_id: int = int(_brush_popup.get_item_metadata(id))
	_brush_popup.hide()
	for it in _type_options:
		if int(it.get("id", 0)) == type_id:
			_brush_input.text = str(it.get("slug_id", ""))
			break
	brush_chosen.emit(type_id)