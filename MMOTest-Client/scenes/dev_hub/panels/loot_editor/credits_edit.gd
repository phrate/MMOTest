class_name LootEditorCreditsEdit
extends VBoxContainer
## Right-side editor for a loot_lists_credits row: slug + two spinboxes + Save + Delete.

signal save_requested(min_credits: int, max_credits: int, slug_id: String)
signal delete_requested(id: int, slug: String)

var _src_id: int = 0
var _src_slug: String = ""
var _slug_edit: LineEdit
var _min_spin: SpinBox
var _max_spin: SpinBox
var _save_btn: Button
var _delete_btn: Button
var _loading_overlay: Control
var _hint: Label
var _save_state_reset_id: int = 0


func _init() -> void:
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)
	_build_ui()


func set_data(data: Dictionary) -> void:
	_src_id = int(data.get("id", 0))
	_src_slug = str(data.get("slug_id", ""))
	_slug_edit.text = _src_slug
	_min_spin.set_value_no_signal(int(data.get("min_credits", 0)))
	_max_spin.set_value_no_signal(int(data.get("max_credits", 0)))
	_save_btn.disabled = false
	_delete_btn.disabled = false
	_hint.visible = true
	set_loading(false)


func clear() -> void:
	_src_id = 0
	_src_slug = ""
	_slug_edit.text = ""
	_min_spin.set_value_no_signal(0)
	_max_spin.set_value_no_signal(0)
	if _save_btn != null and is_instance_valid(_save_btn): _save_btn.disabled = true
	if _delete_btn != null and is_instance_valid(_delete_btn): _delete_btn.disabled = true
	_hint.visible = false
	set_loading(false)


func set_loading(v: bool) -> void:
	if _loading_overlay != null: _loading_overlay.visible = v


func set_save_in_flight() -> void:
	_save_btn.disabled = true
	_save_btn.text = Texts.get_text("ui.dev_hub.loot_editor.credits_saving")


func set_save_result(ok: bool, id_matches: bool) -> void:
	if not id_matches: return
	_save_btn.text = Texts.get_text("ui.dev_hub.loot_editor.credits_saved") if ok \
		else Texts.get_text("ui.dev_hub.loot_editor.credits_save_failed")
	_save_state_reset_id += 1
	var my_reset := _save_state_reset_id
	await get_tree().create_timer(1.5).timeout
	if _save_state_reset_id != my_reset: return
	if not is_instance_valid(_save_btn): return
	_save_btn.text = Texts.get_text("ui.dev_hub.loot_editor.credits_save_button")
	_save_btn.disabled = false


func _build_ui() -> void:
	var s := DevStyle.instance

	var header := Label.new()
	header.text = Texts.get_text("ui.dev_hub.loot_editor.credits_header")
	header.add_theme_font_size_override("font_size", s.font_size_tab)
	header.add_theme_color_override("font_color", s.text_heading)
	add_child(header)

	_hint = Label.new()
	_hint.text = Texts.get_text("ui.dev_hub.loot_editor.select_prompt")
	_hint.add_theme_font_size_override("font_size", s.font_size_small)
	_hint.add_theme_color_override("font_color", s.text_muted)
	add_child(_hint)

	var slug_row := HBoxContainer.new()
	slug_row.add_theme_constant_override("separation", 8)
	add_child(slug_row)
	slug_row.add_child(DevFormFields.label(Texts.get_text("ui.dev_hub.loot_editor.slug_label")))
	_slug_edit = LineEdit.new()
	_slug_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_slug_edit.placeholder_text = Texts.get_text("ui.dev_hub.loot_editor.slug_placeholder")
	slug_row.add_child(_slug_edit)

	var row_min := HBoxContainer.new()
	row_min.add_theme_constant_override("separation", 8)
	add_child(row_min)
	row_min.add_child(DevFormFields.label(Texts.get_text("ui.dev_hub.loot_editor.credits_label_min")))
	_min_spin = SpinBox.new()
	_min_spin.min_value = 0
	_min_spin.max_value = 99999999
	_min_spin.step = 1
	_min_spin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row_min.add_child(_min_spin)

	var row_max := HBoxContainer.new()
	row_max.add_theme_constant_override("separation", 8)
	add_child(row_max)
	row_max.add_child(DevFormFields.label(Texts.get_text("ui.dev_hub.loot_editor.credits_label_max")))
	_max_spin = SpinBox.new()
	_max_spin.min_value = 0
	_max_spin.max_value = 99999999
	_max_spin.step = 1
	_max_spin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row_max.add_child(_max_spin)

	var btn_row := HBoxContainer.new()
	btn_row.add_theme_constant_override("separation", 8)
	add_child(btn_row)

	_save_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.loot_editor.credits_save_button"),
		Color(0.28, 0.62, 0.28), 44, 16)
	_save_btn.disabled = true
	_save_btn.pressed.connect(_on_save_pressed)
	btn_row.add_child(_save_btn)

	_delete_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.loot_editor.credits_delete_button"),
		Color(0.62, 0.20, 0.20), 44, 16)
	_delete_btn.disabled = true
	_delete_btn.pressed.connect(_on_delete_pressed)
	btn_row.add_child(_delete_btn)

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


func _on_save_pressed() -> void:
	if _src_id <= 0: return
	var mn := DevFormFields.read_spin(_min_spin)
	var mx := DevFormFields.read_spin(_max_spin)
	var slug := _slug_edit.text.strip_edges()
	save_requested.emit(mn, mx, slug)


func _on_delete_pressed() -> void:
	if _src_id <= 0: return
	delete_requested.emit(_src_id, _src_slug)
