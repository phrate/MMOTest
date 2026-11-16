class_name DevFormFields
extends RefCounted
## Static factories for form field rows. Callers pass an `wdict: Dictionary`
## that receives `save_key -> widget` so the save handler can walk widgets
## back into a payload dictionary without knowing which widget type each row
## holds.
##
## Convention: every helper takes (container, label_text, save_key, ..., wdict).
## save_key is the payload field name; label_text is already-localized UI text
## (Texts lookup is the caller's job).

const LABEL_WIDTH := 140
const ROW_SEP := 6
const LINE_EDIT_WIDTH := 240
const SPIN_WIDTH := 100
const DROPDOWN_WIDTH := 180
const TEXTAREA_MIN_HEIGHT := 80


static func field_row() -> HBoxContainer:
	var r := HBoxContainer.new()
	r.add_theme_constant_override("separation", ROW_SEP)
	return r


static func label(text: String) -> Label:
	var s := DevStyle.instance
	var l := Label.new()
	l.text = text
	l.custom_minimum_size = Vector2(LABEL_WIDTH, 0)
	l.clip_text = true
	l.add_theme_font_size_override("font_size", s.font_size_small)
	l.add_theme_color_override("font_color", s.text_muted)
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	return l


static func compact_label(text: String) -> Label:
	var s := DevStyle.instance
	var l := Label.new()
	l.text = text
	l.add_theme_font_size_override("font_size", s.font_size_small)
	l.add_theme_color_override("font_color", s.text_muted)
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	return l


static func spin(minv: int, maxv: int, val: int, width: int = SPIN_WIDTH) -> SpinBox:
	var s := DevStyle.instance
	var sb := SpinBox.new()
	sb.min_value = minv
	sb.max_value = maxv
	sb.step = 1
	sb.value = val
	sb.update_on_text_changed = true
	sb.custom_minimum_size = Vector2(width, 0)
	sb.add_theme_font_size_override("font_size", s.font_size_small)
	return sb


static func line_edit(container: VBoxContainer, label_text: String, value: String,
					  readonly: bool, wdict: Dictionary, save_key: String,
					  placeholder: String = "") -> void:
	var s := DevStyle.instance
	var row := field_row()
	row.add_child(label(label_text))
	var le := LineEdit.new()
	le.text = value
	le.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, 0)
	le.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	le.add_theme_font_size_override("font_size", s.font_size_small)
	if readonly:
		le.editable = false
	if not placeholder.is_empty():
		le.placeholder_text = placeholder
	row.add_child(le)
	container.add_child(row)
	wdict[save_key] = le


static func textarea(container: VBoxContainer, label_text: String, value: String,
					 wdict: Dictionary, save_key: String) -> void:
	var s := DevStyle.instance
	var row := field_row()
	row.add_child(label(label_text))
	var te := TextEdit.new()
	te.text = value
	te.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, TEXTAREA_MIN_HEIGHT)
	te.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	te.add_theme_font_size_override("font_size", s.font_size_small)
	row.add_child(te)
	container.add_child(row)
	wdict[save_key] = te


static func spin_row(container: VBoxContainer, label_text: String, val: int,
					 minv: int, maxv: int, wdict: Dictionary, save_key: String) -> void:
	var row := field_row()
	row.add_child(label(label_text))
	var sb := spin(minv, maxv, val)
	row.add_child(sb)
	container.add_child(row)
	wdict[save_key] = sb


static func inline_spin(row: HBoxContainer, label_text: String, val: int,
						wdict: Dictionary, save_key: String) -> void:
	row.add_child(compact_label(label_text))
	var sb := spin(-999999, 999999, val)
	row.add_child(sb)
	wdict[save_key] = sb


static func dropdown(container: VBoxContainer, label_text: String, current: String,
					 options: Array, wdict: Dictionary, save_key: String) -> void:
	var s := DevStyle.instance
	var row := field_row()
	row.add_child(label(label_text))
	var dd := OptionButton.new()
	dd.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	dd.add_theme_font_size_override("font_size", s.font_size_small)
	dd.fit_to_longest_item = false
	dd.custom_minimum_size = Vector2(DROPDOWN_WIDTH, 0)
	var found := false
	for opt in options:
		var opt_str := str(opt)
		var idx := dd.item_count
		dd.add_item(opt_str)
		dd.set_item_metadata(idx, opt_str)
		if opt_str == current:
			dd.select(idx)
			found = true
	if not found and dd.item_count > 0:
		dd.select(0)
	row.add_child(dd)
	container.add_child(row)
	wdict[save_key] = dd

static func compact_line_edit(row: HBoxContainer, label_text: String, value: String,
							  readonly: bool, wdict: Dictionary, save_key: String,
							  placeholder: String = "") -> void:
	var s := DevStyle.instance
	row.add_child(compact_label(label_text))
	var le := LineEdit.new()
	le.text = value
	le.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, 0)
	le.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	le.add_theme_font_size_override("font_size", s.font_size_small)
	if readonly:
		le.editable = false
	if not placeholder.is_empty():
		le.placeholder_text = placeholder
	row.add_child(le)
	wdict[save_key] = le


static func compact_textarea(row: HBoxContainer, label_text: String, value: String,
							 wdict: Dictionary, save_key: String) -> void:
	var s := DevStyle.instance
	row.add_child(compact_label(label_text))
	var te := TextEdit.new()
	te.text = value
	te.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, TEXTAREA_MIN_HEIGHT)
	te.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	te.add_theme_font_size_override("font_size", s.font_size_small)
	row.add_child(te)
	wdict[save_key] = te


static func compact_spin_row(row: HBoxContainer, label_text: String, val: int,
							 minv: int, maxv: int, wdict: Dictionary, save_key: String) -> void:
	row.add_child(compact_label(label_text))
	var sb := spin(minv, maxv, val)
	row.add_child(sb)
	wdict[save_key] = sb


static func compact_dropdown(row: HBoxContainer, label_text: String, current: String,
							 options: Array, wdict: Dictionary, save_key: String,
							 label_to_value: Dictionary = {}) -> void:
	var s := DevStyle.instance
	row.add_child(compact_label(label_text))
	var dd := OptionButton.new()
	dd.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	dd.add_theme_font_size_override("font_size", s.font_size_small)
	dd.fit_to_longest_item = false
	dd.custom_minimum_size = Vector2(DROPDOWN_WIDTH, 0)
	var found := false
	for opt in options:
		var opt_str := str(opt)
		var idx := dd.item_count
		dd.add_item(opt_str)
		var value: String = str(label_to_value.get(opt_str, opt_str))
		dd.set_item_metadata(idx, value)
		if value == current or opt_str == current:
			dd.select(idx)
			found = true
	if not found:
		if current == "":
			dd.select(-1)
		elif dd.item_count > 0:
			dd.select(0)
	row.add_child(dd)
	wdict[save_key] = dd

static func action_button(text: String, base: Color, height: int = 48, font_size: int = 18) -> Button:
	var s := DevStyle.instance
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	b.custom_minimum_size = Vector2(0, height)
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.add_theme_font_size_override("font_size", font_size)
	b.add_theme_color_override("font_color", Color.WHITE)
	b.add_theme_color_override("font_hover_color", Color.WHITE)
	b.add_theme_color_override("font_pressed_color", Color.WHITE)
	b.add_theme_color_override("font_disabled_color", Color(0.75, 0.75, 0.75))
	b.add_theme_stylebox_override("normal", s.make_button_style(base))
	b.add_theme_stylebox_override("hover", s.make_button_style(base.lightened(0.12)))
	b.add_theme_stylebox_override("pressed", s.make_button_style(base.darkened(0.12)))
	b.add_theme_stylebox_override("disabled", s.make_button_style(base.darkened(0.35)))
	return b


static func collect_payload(wdict: Dictionary, baseline: Dictionary) -> Dictionary:
	## Walk widgets back into a payload dict. Every widget type is handled;
	## callers add per-field post-processing (e.g. inverted flags) afterwards.
	var data := baseline.duplicate()
	for key in wdict.keys():
		var w = wdict[key]
		if w is SpinBox:
			data[key] = int((w as SpinBox).value)
		elif w is LineEdit:
			data[key] = (w as LineEdit).text
		elif w is TextEdit:
			data[key] = (w as TextEdit).text
		elif w is OptionButton:
			var idx: int = (w as OptionButton).selected
			if idx >= 0:
				var md = (w as OptionButton).get_item_metadata(idx)
				data[key] = str(md) if md != null else ""
			else:
				data[key] = ""
		elif w is Button:
			data[key] = (w as Button).button_pressed
	return data
