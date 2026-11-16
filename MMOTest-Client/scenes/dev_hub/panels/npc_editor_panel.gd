extends DevPanel
## NPC editor root — sub-tabs Browse / Edit / Create / Render.
## Owns: network wiring, tab dispatch, save/delete button state.
## Delegates: Browse list (NpcEditorBrowse), type form (NpcEditorForm),
## Render tab (NpcEditorRender).

const TAB_BROWSE := 0
const TAB_EDIT := 1
const TAB_CREATE := 2
const TAB_RENDER := 3

@export var save_button_height: int = 48
@export var save_button_font_size: int = 18

var _subtab_container: TabContainer
var _browse: NpcEditorBrowse
var _edit_form: NpcEditorForm
var _create_form: NpcEditorForm
var _render: NpcEditorRender
var _edit_save_btn: Button
var _edit_delete_btn: Button
var _create_submit_btn: Button
var _create_error_label: Label

var _current_type_id: int = 0
var _current_data: Dictionary = {}
var _options: Dictionary = {}
var _type_list: Array = []
var _options_loaded: bool = false
var _pending_type_id: int = 0


func _ready() -> void:
	_build_ui()
	_connect_network()
	_request_options()
	_request_type_list()
	_subtab_container.current_tab = TAB_BROWSE


func panel_opened() -> void:
	if not _options_loaded:
		_request_options()
	_request_type_list()


func on_hub_open_for_entity(entity_type: String, entity_id: int) -> void:
	if entity_type != "npc_type": return
	_pending_type_id = entity_id
	Network.send_dev_npc_type_get(entity_id)


# ============================================================
# Network
# ============================================================

func _connect_network() -> void:
	Network.dev_npc_editor_options_result.connect(_on_options_result)
	Network.dev_npc_type_list_result.connect(_on_type_list_result)
	Network.dev_npc_type_data.connect(_on_type_data)
	Network.dev_npc_type_created.connect(_on_type_created)
	Network.dev_npc_type_updated.connect(_on_type_updated)
	Network.dev_npc_type_deleted.connect(_on_type_deleted)


func _request_options() -> void:
	Network.send_dev_npc_editor_options_request()


func _request_type_list() -> void:
	Network.send_dev_npc_type_list()


func _on_options_result(ok: bool, options: Dictionary) -> void:
	if not ok: return
	_options = options
	_options_loaded = true
	if _current_type_id != 0:
		_edit_form.set_data(_current_data, true, _options)
	_refresh_render_slugs()


func _on_type_list_result(ok: bool, items: Array) -> void:
	if not ok: return
	_type_list = items
	_browse.set_types(items)
	_refresh_render_slugs()


func _refresh_render_slugs() -> void:
	var counts := {}
	for it in _type_list:
		if typeof(it) != TYPE_DICTIONARY: continue
		var rs := str(it.get("render_slug", ""))
		if rs.is_empty(): continue
		counts[rs] = int(counts.get(rs, 0)) + 1
	var slugs: Array = (_options.get("render_slugs", []) as Array).duplicate()
	_render.set_slugs(slugs, counts)


func _on_type_data(ok: bool, data: Dictionary) -> void:
	if not ok:
		_pending_type_id = 0
		return
	_current_type_id = int(data.get("id", 0))
	_current_data = data
	_edit_form.set_data(data, true, _options)
	_edit_save_btn.disabled = false
	_edit_delete_btn.disabled = false
	_pending_type_id = 0
	_subtab_container.current_tab = TAB_EDIT


func _on_type_created(ok: bool, type_id: int, error: String) -> void:
	_create_submit_btn.disabled = false
	if ok:
		_create_error_label.visible = false
		_pending_type_id = type_id
		Network.send_dev_npc_type_get(type_id)
	else:
		_show_create_error(error if error != "" else Texts.get_text("ui.dev_hub.npc_editor.error_create_failed"))


func _on_type_updated(ok: bool, type_id: int) -> void:
	if type_id != _current_type_id: return
	_edit_save_btn.disabled = false
	if ok:
		_edit_save_btn.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_saved")
		Network.send_dev_npc_type_get(type_id)
		await get_tree().create_timer(1.2).timeout
		if _edit_save_btn and _edit_save_btn.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_saved"):
			_edit_save_btn.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")
	else:
		_edit_save_btn.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_failed")
		await get_tree().create_timer(2.0).timeout
		if _edit_save_btn and _edit_save_btn.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_failed"):
			_edit_save_btn.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")


func _on_type_deleted(ok: bool, type_id: int) -> void:
	if type_id != _current_type_id: return
	if not ok: return
	_current_type_id = 0
	_current_data = {}
	_subtab_container.current_tab = TAB_BROWSE
	_request_type_list()


# ============================================================
# UI
# ============================================================

func _build_ui() -> void:
	var s := DevStyle.instance
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", s.panel_separation)
	add_child(vbox)

	_subtab_container = TabContainer.new()
	_subtab_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_subtab_container.size_flags_vertical = Control.SIZE_EXPAND_FILL
	vbox.add_child(_subtab_container)

	_browse = NpcEditorBrowse.new()
	_browse.type_selected.connect(_on_browse_type_selected)
	_browse.create_requested.connect(_on_browse_create)
	_subtab_container.add_child(_browse)

	_subtab_container.add_child(_build_edit_tab())
	_subtab_container.add_child(_build_create_tab())

	_render = NpcEditorRender.new()
	_subtab_container.add_child(_render)

	_subtab_container.set_tab_title(TAB_BROWSE, Texts.get_text("ui.dev_hub.npc_editor.tab_browse"))
	_subtab_container.set_tab_title(TAB_EDIT, Texts.get_text("ui.dev_hub.npc_editor.tab_edit"))
	_subtab_container.set_tab_title(TAB_CREATE, Texts.get_text("ui.dev_hub.npc_editor.tab_create"))
	_subtab_container.set_tab_title(TAB_RENDER, Texts.get_text("ui.dev_hub.npc_editor.tab_render"))


func _build_edit_tab() -> Control:
	var s := DevStyle.instance
	var edit := VBoxContainer.new()
	edit.name = "Edit"
	edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	edit.size_flags_vertical = Control.SIZE_EXPAND_FILL
	edit.add_theme_constant_override("separation", s.panel_separation)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	edit.add_child(scroll)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", s.panel_content_margin)
	margin.add_theme_constant_override("margin_right", s.panel_content_margin)
	margin.add_theme_constant_override("margin_top", s.panel_content_margin)
	margin.add_theme_constant_override("margin_bottom", s.panel_content_margin)
	margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(margin)

	_edit_form = NpcEditorForm.new()
	margin.add_child(_edit_form)

	var btn_row := HBoxContainer.new()
	btn_row.add_theme_constant_override("separation", s.panel_separation)
	edit.add_child(btn_row)

	_edit_save_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.npc_editor.save_all"),
		Color(0.28, 0.62, 0.28), save_button_height, save_button_font_size)
	_edit_save_btn.disabled = true
	_edit_save_btn.pressed.connect(_on_edit_save_pressed)
	btn_row.add_child(_edit_save_btn)

	_edit_delete_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.npc_editor.delete_button"),
		s.button_close, save_button_height, save_button_font_size)
	_edit_delete_btn.disabled = true
	_edit_delete_btn.pressed.connect(_on_edit_delete_pressed)
	btn_row.add_child(_edit_delete_btn)

	return edit


func _build_create_tab() -> Control:
	var s := DevStyle.instance
	var create := VBoxContainer.new()
	create.name = "Create"
	create.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	create.size_flags_vertical = Control.SIZE_EXPAND_FILL
	create.add_theme_constant_override("separation", s.panel_separation)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	create.add_child(scroll)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", s.panel_content_margin)
	margin.add_theme_constant_override("margin_right", s.panel_content_margin)
	margin.add_theme_constant_override("margin_top", s.panel_content_margin)
	margin.add_theme_constant_override("margin_bottom", s.panel_content_margin)
	margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(margin)

	_create_form = NpcEditorForm.new()
	margin.add_child(_create_form)

	_create_error_label = Label.new()
	_create_error_label.add_theme_font_size_override("font_size", s.font_size_small)
	_create_error_label.add_theme_color_override("font_color", Color(1.0, 0.4, 0.4))
	_create_error_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_create_error_label.visible = false
	create.add_child(_create_error_label)

	_create_submit_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.npc_editor.create_button"),
		Color(0.28, 0.62, 0.28), save_button_height, save_button_font_size)
	_create_submit_btn.pressed.connect(_on_create_pressed)
	create.add_child(_create_submit_btn)

	_create_form.set_data({}, false, {})
	return create


# ============================================================
# Dispatch
# ============================================================

func _on_browse_type_selected(type_id: int) -> void:
	if _pending_type_id == type_id: return
	_pending_type_id = type_id
	Network.send_dev_npc_type_get(type_id)


func _on_browse_create() -> void:
	_create_form.set_data({}, false, _options)
	_create_error_label.visible = false
	_subtab_container.current_tab = TAB_CREATE


func _on_edit_save_pressed() -> void:
	if _current_type_id == 0: return
	_edit_save_btn.disabled = true
	_edit_save_btn.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_saving")
	Network.send_dev_npc_type_update(_current_type_id, _edit_form.get_payload())


func _on_edit_delete_pressed() -> void:
	if _current_type_id == 0: return
	var dlg := ConfirmationDialog.new()
	dlg.dialog_text = Texts.get_text("ui.dev_hub.npc_editor.delete_confirm")
	dlg.title = Texts.get_text("ui.dev_hub.npc_editor.delete_button")
	dlg.ok_button_text = "Delete"
	dlg.confirmed.connect(func():
		Network.send_dev_npc_type_delete(_current_type_id)
		dlg.queue_free())
	dlg.canceled.connect(func(): dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()


func _on_create_pressed() -> void:
	_create_error_label.visible = false
	var data := _create_form.get_payload()
	var slug := str(data.get("slug_id", "")).strip_edges()
	var display := str(data.get("display", "")).strip_edges()
	var render := str(data.get("render_slug", "")).strip_edges()
	if slug.is_empty():
		_show_create_error(Texts.get_text("ui.dev_hub.npc_editor.error_slug_required"))
		return
	if display.is_empty():
		_show_create_error(Texts.get_text("ui.dev_hub.npc_editor.error_display_required"))
		return
	if render.is_empty():
		_show_create_error(Texts.get_text("ui.dev_hub.npc_editor.error_render_required"))
		return
	data["slug_id"] = slug
	data["display"] = display
	_create_submit_btn.disabled = true
	Network.send_dev_npc_type_create(data)


func _show_create_error(msg: String) -> void:
	_create_error_label.text = msg
	_create_error_label.visible = true
