class_name LootEditorItemsEdit
extends VBoxContainer
## Right-side editor for a loot_list's item_bag. Alternating-row table with
## icon + slug + min/max + remove. Slug editable at the top. Save commits
## everything together.

signal save_requested(items: Array, slug_id: String)
signal search_requested(query: String)
signal delete_requested(id: int, slug: String)

const MIN_SUGGEST_CHARS := 3

var _header: Label
var _slug_edit: LineEdit
var _scroll: ScrollContainer
var _rows_container: VBoxContainer
var _empty_hint: Label
var _add_slug: LineEdit
var _add_min: SpinBox
var _add_max: SpinBox
var _save_btn: Button
var _delete_btn: Button
var _loading_overlay: Control

var _suggest_panel: PanelContainer
var _suggest_vbox: VBoxContainer

var _src_id: int = 0
var _src_slug: String = ""
var _entries: Array = []          # [{slug, min, max}]
var _row_widgets: Array = []      # [{panel, min_spin, max_spin, slug}]
var _search_debounce: Timer
var _save_state_reset_id: int = 0


func _init() -> void:
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)
	_build_ui()


func _ready() -> void:
	_search_debounce = Timer.new()
	_search_debounce.wait_time = 0.3
	_search_debounce.one_shot = true
	add_child(_search_debounce)
	_search_debounce.timeout.connect(_on_search_timeout)
	_add_slug.text_changed.connect(func(_t: String) -> void: _search_debounce.start())
	if ItemIcons:
		ItemIcons.icon_ready.connect(_on_icon_ready)


func set_data(data: Dictionary) -> void:
	_src_id = int(data.get("id", 0))
	_src_slug = str(data.get("slug_id", ""))
	_slug_edit.text = _src_slug
	_entries.clear()
	for entry in data.get("item_bag", []):
		if typeof(entry) != TYPE_DICTIONARY: continue
		_entries.append({
			"slug": str(entry.get("slug", "")),
			"min": int(entry.get("min", 0)),
			"max": int(entry.get("max", 0)),
		})
	_rebuild_rows()
	_save_btn.disabled = false
	_delete_btn.disabled = false
	set_loading(false)


func clear() -> void:
	_src_id = 0
	_src_slug = ""
	_slug_edit.text = ""
	_entries.clear()
	_rebuild_rows()
	if _save_btn != null and is_instance_valid(_save_btn): _save_btn.disabled = true
	if _delete_btn != null and is_instance_valid(_delete_btn): _delete_btn.disabled = true
	set_loading(false)


func set_loading(v: bool) -> void:
	if _loading_overlay != null: _loading_overlay.visible = v


func set_save_in_flight() -> void:
	_save_btn.disabled = true
	_save_btn.text = Texts.get_text("ui.dev_hub.loot_editor.items_saving")


func set_save_result(ok: bool, id_matches: bool) -> void:
	if not id_matches: return
	_save_btn.text = Texts.get_text("ui.dev_hub.loot_editor.items_saved") if ok \
		else Texts.get_text("ui.dev_hub.loot_editor.items_save_failed")
	_save_state_reset_id += 1
	var my_reset := _save_state_reset_id
	await get_tree().create_timer(1.5).timeout
	if _save_state_reset_id != my_reset: return
	if not is_instance_valid(_save_btn): return
	_save_btn.text = Texts.get_text("ui.dev_hub.loot_editor.items_save_button")
	_save_btn.disabled = false


func _on_icon_ready(_name: String) -> void:
	if _src_id <= 0: return
	if _entries.is_empty(): return
	var focused := get_viewport().gui_get_focus_owner()
	if focused is SpinBox or focused is LineEdit: return
	_rebuild_rows()


# ---------- Build ----------

func _build_ui() -> void:
	var s := DevStyle.instance

	_header = Label.new()
	_header.text = Texts.get_text("ui.dev_hub.loot_editor.items_header")
	_header.add_theme_font_size_override("font_size", s.font_size_tab)
	_header.add_theme_color_override("font_color", s.text_heading)
	add_child(_header)

	var slug_row := HBoxContainer.new()
	slug_row.add_theme_constant_override("separation", 6)
	add_child(slug_row)
	slug_row.add_child(DevFormFields.compact_label(Texts.get_text("ui.dev_hub.loot_editor.slug_label")))
	_slug_edit = LineEdit.new()
	_slug_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_slug_edit.placeholder_text = Texts.get_text("ui.dev_hub.loot_editor.slug_placeholder")
	slug_row.add_child(_slug_edit)

	_scroll = ScrollContainer.new()
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_scroll.custom_minimum_size = Vector2(0, 260)
	add_child(_scroll)

	_rows_container = VBoxContainer.new()
	_rows_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_rows_container.add_theme_constant_override("separation", 2)
	_scroll.add_child(_rows_container)

	_empty_hint = Label.new()
	_empty_hint.text = Texts.get_text("ui.dev_hub.loot_editor.items_empty")
	_empty_hint.add_theme_font_size_override("font_size", s.font_size_small)
	_empty_hint.add_theme_color_override("font_color", s.text_muted)
	add_child(_empty_hint)

	var add_header := Label.new()
	add_header.text = Texts.get_text("ui.dev_hub.loot_editor.items_add_header")
	add_header.add_theme_font_size_override("font_size", s.font_size_small)
	add_header.add_theme_color_override("font_color", s.text_heading)
	add_child(add_header)

	var add_row := HBoxContainer.new()
	add_row.add_theme_constant_override("separation", 6)
	add_child(add_row)

	_add_slug = LineEdit.new()
	_add_slug.placeholder_text = Texts.get_text("ui.dev_hub.loot_editor.items_slug_placeholder")
	_add_slug.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add_row.add_child(_add_slug)

	add_row.add_child(DevFormFields.compact_label(Texts.get_text("ui.dev_hub.loot_editor.items_col_min")))
	_add_min = SpinBox.new()
	_add_min.min_value = 1
	_add_min.max_value = 999999
	_add_min.value = 1
	_add_min.custom_minimum_size = Vector2(80, 0)
	add_row.add_child(_add_min)

	add_row.add_child(DevFormFields.compact_label(Texts.get_text("ui.dev_hub.loot_editor.items_col_max")))
	_add_max = SpinBox.new()
	_add_max.min_value = 1
	_add_max.max_value = 999999
	_add_max.value = 1
	_add_max.custom_minimum_size = Vector2(80, 0)
	add_row.add_child(_add_max)

	var add_btn := DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.loot_editor.items_add_button"),
		Color(0.28, 0.35, 0.62), 36, 14)
	add_btn.size_flags_horizontal = Control.SIZE_SHRINK_END
	add_btn.pressed.connect(_on_add_pressed)
	add_row.add_child(add_btn)

	var btn_row := HBoxContainer.new()
	btn_row.add_theme_constant_override("separation", 8)
	add_child(btn_row)

	_save_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.loot_editor.items_save_button"),
		Color(0.28, 0.62, 0.28), 44, 16)
	_save_btn.disabled = true
	_save_btn.pressed.connect(_on_save_pressed)
	btn_row.add_child(_save_btn)

	_delete_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.loot_editor.items_delete_button"),
		Color(0.62, 0.20, 0.20), 44, 16)
	_delete_btn.disabled = true
	_delete_btn.pressed.connect(_on_delete_pressed)
	btn_row.add_child(_delete_btn)

	_suggest_panel = PanelContainer.new()
	_suggest_panel.top_level = true
	_suggest_panel.visible = false
	_suggest_panel.mouse_filter = Control.MOUSE_FILTER_STOP
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
	_suggest_panel.add_theme_stylebox_override("panel", sb)
	add_child(_suggest_panel)

	_suggest_vbox = VBoxContainer.new()
	_suggest_vbox.add_theme_constant_override("separation", 2)
	_suggest_panel.add_child(_suggest_vbox)

	_loading_overlay = Control.new()
	_loading_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_loading_overlay.mouse_filter = Control.MOUSE_FILTER_STOP
	_loading_overlay.visible = false
	add_child(_loading_overlay)

	var dim := ColorRect.new()
	dim.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	dim.color = Color(0, 0, 0, 0.55)
	dim.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_loading_overlay.add_child(dim)

	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_loading_overlay.add_child(center)

	var load_lbl := Label.new()
	load_lbl.text = Texts.get_text("ui.dev_hub.loot_editor.loading_data")
	load_lbl.add_theme_font_size_override("font_size", s.font_size_title)
	load_lbl.add_theme_color_override("font_color", s.text_heading)
	center.add_child(load_lbl)


# ---------- Rows ----------

func _rebuild_rows() -> void:
	for c in _rows_container.get_children():
		_rows_container.remove_child(c)
		c.queue_free()
	_row_widgets.clear()
	_empty_hint.visible = _entries.is_empty()

	for i in _entries.size():
		_row_widgets.append(_make_row(_entries[i], i))


func _make_row(entry: Dictionary, index: int) -> Dictionary:
	var s := DevStyle.instance
	var panel := PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.18, 0.18, 0.22, 1.0) if index % 2 == 0 else Color(0.14, 0.14, 0.17, 1.0)
	style.content_margin_left = 8
	style.content_margin_right = 8
	style.content_margin_top = 4
	style.content_margin_bottom = 4
	style.corner_radius_top_left = 3
	style.corner_radius_top_right = 3
	style.corner_radius_bottom_left = 3
	style.corner_radius_bottom_right = 3
	panel.add_theme_stylebox_override("panel", style)
	_rows_container.add_child(panel)

	var hbox := HBoxContainer.new()
	hbox.add_theme_constant_override("separation", 8)
	panel.add_child(hbox)

	var slug := str(entry.get("slug", ""))

	var icon_box := Control.new()
	icon_box.custom_minimum_size = Vector2(24, 24)
	hbox.add_child(icon_box)

	var tex: Texture2D = null
	if ItemIcons and not slug.is_empty():
		tex = ItemIcons.get_texture(slug)
	if tex != null:
		var tr := TextureRect.new()
		tr.texture = tex
		tr.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
		tr.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
		tr.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
		icon_box.add_child(tr)
	else:
		var letter := Label.new()
		letter.text = slug.substr(0, 1).to_upper() if not slug.is_empty() else "?"
		letter.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		letter.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		letter.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
		letter.add_theme_font_size_override("font_size", 13)
		icon_box.add_child(letter)

	var slug_lbl := Label.new()
	slug_lbl.text = slug
	slug_lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	slug_lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	slug_lbl.add_theme_font_size_override("font_size", s.font_size_small)
	hbox.add_child(slug_lbl)

	hbox.add_child(DevFormFields.compact_label(Texts.get_text("ui.dev_hub.loot_editor.items_col_min")))
	var min_spin := SpinBox.new()
	min_spin.min_value = 0
	min_spin.max_value = 999999
	min_spin.value = int(entry.get("min", 0))
	min_spin.custom_minimum_size = Vector2(80, 0)
	min_spin.add_theme_font_size_override("font_size", s.font_size_small)
	min_spin.value_changed.connect(func(v: float) -> void:
		if index >= 0 and index < _entries.size():
			_entries[index]["min"] = int(v)
			if _entries[index]["max"] < int(v):
				_entries[index]["max"] = int(v))
	hbox.add_child(min_spin)

	hbox.add_child(DevFormFields.compact_label(Texts.get_text("ui.dev_hub.loot_editor.items_col_max")))
	var max_spin := SpinBox.new()
	max_spin.min_value = 0
	max_spin.max_value = 999999
	max_spin.value = int(entry.get("max", 0))
	max_spin.custom_minimum_size = Vector2(80, 0)
	max_spin.add_theme_font_size_override("font_size", s.font_size_small)
	max_spin.value_changed.connect(func(v: float) -> void:
		if index >= 0 and index < _entries.size():
			_entries[index]["max"] = int(v)
			if _entries[index]["min"] > int(v):
				_entries[index]["min"] = int(v))
	hbox.add_child(max_spin)

	var remove_btn := Button.new()
	remove_btn.text = "×"
	remove_btn.custom_minimum_size = Vector2(32, 0)
	remove_btn.focus_mode = Control.FOCUS_NONE
	remove_btn.pressed.connect(func() -> void:
		if index >= 0 and index < _entries.size():
			_entries.remove_at(index)
			_rebuild_rows())
	hbox.add_child(remove_btn)

	return {"panel": panel, "min_spin": min_spin, "max_spin": max_spin, "slug": slug}


# ---------- Add ----------

func _on_add_pressed() -> void:
	var slug := _add_slug.text.strip_edges()
	if slug.is_empty(): return
	var mn := DevFormFields.read_spin(_add_min)
	var mx := DevFormFields.read_spin(_add_max)
	if mx < mn: mx = mn

	for existing in _entries:
		if str(existing.get("slug", "")) == slug:
			existing["min"] = int(existing["min"]) + mn
			existing["max"] = int(existing["max"]) + mx
			_add_slug.clear()
			_hide_suggestions()
			_rebuild_rows()
			call_deferred("_scroll_to_bottom")
			return

	_entries.append({"slug": slug, "min": mn, "max": mx})
	_add_slug.clear()
	_add_min.value = 1
	_add_max.value = 1
	_hide_suggestions()
	_rebuild_rows()
	call_deferred("_scroll_to_bottom")


func _scroll_to_bottom() -> void:
	if _scroll == null: return
	var vbar := _scroll.get_v_scroll_bar()
	if vbar == null: return
	_scroll.scroll_vertical = int(vbar.max_value)


func _on_search_timeout() -> void:
	var text := _add_slug.text.strip_edges()
	if text.length() < MIN_SUGGEST_CHARS:
		_hide_suggestions()
		return
	search_requested.emit(text)


# ---------- Suggestions ----------

func set_item_search_results(items: Array) -> void:
	_clear_suggestions()
	if items.is_empty():
		_hide_suggestions()
		return

	var s := DevStyle.instance
	for i in items.size():
		if typeof(items[i]) != TYPE_DICTIONARY: continue
		var entry: Dictionary = items[i]
		var slug := str(entry.get("slug_id", ""))
		var display := str(entry.get("display", ""))
		var btn := Button.new()
		btn.text = "%s  (%s)" % [slug, display]
		btn.alignment = HORIZONTAL_ALIGNMENT_LEFT
		btn.focus_mode = Control.FOCUS_NONE
		btn.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
		btn.add_theme_font_size_override("font_size", s.font_size_small)
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
		btn.pressed.connect(_on_suggestion_pressed.bind(slug))
		_suggest_vbox.add_child(btn)

	_show_suggestions()


func _on_suggestion_pressed(slug: String) -> void:
	_add_slug.text = slug
	_add_slug.caret_column = slug.length()
	_hide_suggestions()


func _show_suggestions() -> void:
	_suggest_panel.reset_size()
	_suggest_panel.global_position = Vector2(
		_add_slug.global_position.x,
		_add_slug.global_position.y + _add_slug.size.y + 2)
	_suggest_panel.visible = true


func _hide_suggestions() -> void:
	_suggest_panel.visible = false


func _clear_suggestions() -> void:
	for c in _suggest_vbox.get_children():
		_suggest_vbox.remove_child(c)
		c.queue_free()


func _input(event: InputEvent) -> void:
	if not _suggest_panel.visible: return
	if not is_visible_in_tree(): return
	if event is InputEventMouseButton and event.pressed:
		if not _suggest_panel.get_global_rect().has_point(event.position):
			_hide_suggestions()
	elif event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_ESCAPE:
			_hide_suggestions()
			get_viewport().set_input_as_handled()


# ---------- Save / delete ----------

func _on_save_pressed() -> void:
	var slug := _slug_edit.text.strip_edges()
	var payload: Array = []
	for w in _row_widgets:
		if typeof(w) != TYPE_DICTIONARY: continue
		var s := str(w.get("slug", ""))
		if s.is_empty(): continue
		var mn := DevFormFields.read_spin(w.get("min_spin"))
		var mx := DevFormFields.read_spin(w.get("max_spin"))
		if mx < mn: mx = mn
		payload.append({"slug": s, "min": mn, "max": mx})
	save_requested.emit(payload, slug)


func _on_delete_pressed() -> void:
	if _src_id <= 0: return
	delete_requested.emit(_src_id, _src_slug)
