extends DevPanel
## Item editor — three sub-tabs: Browse / Edit / Create.
## Composed of: ItemEditorBrowse (Browse), two ItemEditorForm instances
## (Edit + Create). Panel owns network wiring, form lifecycle, and button
## dispatch. Form state flows panel -> form via set_data() and form -> panel
## via get_payload() on save.

const TAB_BROWSE := 0
const TAB_EDIT := 1
const TAB_CREATE := 2

@export var save_button_height: int = 48
@export var save_button_font_size: int = 18

var _subtab_container: TabContainer
var _browse: ItemEditorBrowse
var _edit_form: ItemEditorForm
var _create_form: ItemEditorForm
var _edit_save_btn: Button
var _edit_delete_btn: Button
var _create_submit_btn: Button
var _create_error_label: Label

var _current_item_id: int = 0
var _pending_item_id: int = 0
var _available_tags: Array = []
var _tags_fetched: bool = false
var _pending_give_item_id: int = 0


func _ready() -> void:
	_build_ui()
	_connect_network()
	Network.send_dev_item_list(false)
	_subtab_container.current_tab = TAB_BROWSE
	if ItemIcons:
		ItemIcons.icon_ready.connect(_on_icon_ready)


func panel_opened() -> void:
	Network.send_dev_item_list(_browse.get_include_inactive())
	if ItemIcons and not ItemIcons._sync_in_progress:
		ItemIcons.request_sync()
	if not _tags_fetched:
		Network.send_dev_item_tags_list()


func on_hub_open_for_entity(entity_type: String, entity_id: int) -> void:
	if entity_type != "item": return
	_pending_item_id = entity_id
	Network.send_dev_item_get(entity_id)


# ============================================================
# Network
# ============================================================

func _connect_network() -> void:
	Network.dev_item_list_result.connect(_on_list_result)
	Network.dev_item_data.connect(_on_item_data)
	Network.dev_item_created.connect(_on_item_created)
	Network.dev_item_updated.connect(_on_item_updated)
	Network.dev_item_deleted.connect(_on_item_deleted)
	Network.dev_item_tags_list_result.connect(_on_tags_list_result)
	Network.dev_item_give_result.connect(_on_give_result)


func _on_icon_ready(_name: String) -> void:
	if _current_item_id != 0 and _subtab_container.current_tab == TAB_EDIT:
		_edit_form.refresh_icon()


func _on_tags_list_result(ok: bool, tags: Array) -> void:
	if not ok: return
	_tags_fetched = true
	_available_tags = tags
	if _edit_form:
		_edit_form.set_available_tags(_available_tags)
	if _create_form:
		_create_form.set_available_tags(_available_tags)

func _merge_tags_from_payload(payload: Dictionary) -> void:
	var tags: Variant = payload.get("tags", [])
	if typeof(tags) != TYPE_ARRAY: return
	var changed := false
	for t in tags:
		var tag := str(t)
		if tag.is_empty(): continue
		if _available_tags.has(tag): continue
		_available_tags.append(tag)
		changed = true
	if not changed: return
	_available_tags.sort()
	if _edit_form:
		_edit_form.set_available_tags(_available_tags)
	if _create_form:
		_create_form.set_available_tags(_available_tags)

func _on_list_result(ok: bool, items: Array) -> void:
	if not ok: return
	_browse.set_items(items)


func _on_item_data(ok: bool, data: Dictionary) -> void:
	if not ok:
		_pending_item_id = 0
		return
	_current_item_id = int(data.get("id", 0))
	_edit_form.set_data(data, true)
	_edit_save_btn.disabled = false
	_edit_delete_btn.disabled = false
	_pending_item_id = 0
	_subtab_container.current_tab = TAB_EDIT


func _on_item_created(ok: bool, item_id: int, error: String) -> void:
	_create_submit_btn.disabled = false
	if ok:
		_create_error_label.visible = false
		_merge_tags_from_payload(_create_form.get_payload())
		_pending_item_id = item_id
		Network.send_dev_item_get(item_id)
		Network.send_dev_item_list(_browse.get_include_inactive())
	else:
		_show_create_error(error if error != "" else Texts.get_text("ui.dev_hub.item_editor.error_create_failed"))


func _on_item_updated(ok: bool, item_id: int) -> void:
	if item_id != _current_item_id: return
	_edit_save_btn.disabled = false
	if ok:
		_edit_save_btn.text = Texts.get_text("ui.dev_hub.item_editor.save_saved")
		_merge_tags_from_payload(_edit_form.get_payload())
		Network.send_dev_item_get(item_id)
		Network.send_dev_item_list(_browse.get_include_inactive())
		await get_tree().create_timer(1.2).timeout
		if _edit_save_btn and _edit_save_btn.text == Texts.get_text("ui.dev_hub.item_editor.save_saved"):
			_edit_save_btn.text = Texts.get_text("ui.dev_hub.item_editor.save_button")
	else:
		_edit_save_btn.text = Texts.get_text("ui.dev_hub.item_editor.save_failed")
		await get_tree().create_timer(2.0).timeout
		if _edit_save_btn and _edit_save_btn.text == Texts.get_text("ui.dev_hub.item_editor.save_failed"):
			_edit_save_btn.text = Texts.get_text("ui.dev_hub.item_editor.save_button")


func _on_item_deleted(ok: bool, item_id: int) -> void:
	if item_id != _current_item_id: return
	if not ok: return
	_current_item_id = 0
	_subtab_container.current_tab = TAB_BROWSE
	Network.send_dev_item_list(_browse.get_include_inactive())


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

	_browse = ItemEditorBrowse.new()
	_browse.item_selected.connect(_on_browse_item_selected)
	_browse.create_requested.connect(_on_browse_create)
	_browse.refresh_requested.connect(func(inc: bool) -> void: Network.send_dev_item_list(inc))
	_subtab_container.add_child(_browse)

	_subtab_container.add_child(_build_edit_tab())
	_subtab_container.add_child(_build_create_tab())

	_subtab_container.set_tab_title(TAB_BROWSE, Texts.get_text("ui.dev_hub.item_editor.tab_browse"))
	_subtab_container.set_tab_title(TAB_EDIT, Texts.get_text("ui.dev_hub.item_editor.tab_edit"))
	_subtab_container.set_tab_title(TAB_CREATE, Texts.get_text("ui.dev_hub.item_editor.tab_create"))


func _build_edit_tab() -> Control:
	var s := DevStyle.instance
	var edit := VBoxContainer.new()
	edit.name = "Edit"
	edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	edit.size_flags_vertical = Control.SIZE_EXPAND_FILL
	edit.add_theme_constant_override("separation", s.panel_separation)

	var scroll := _scroll_wrap(edit)

	_edit_form = ItemEditorForm.new()
	_edit_form.give_requested.connect(_on_edit_form_give_requested)
	scroll.add_child(_edit_form)

	var btn_row := HBoxContainer.new()
	btn_row.add_theme_constant_override("separation", s.panel_separation)
	edit.add_child(btn_row)

	_edit_save_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.item_editor.save_button"),
		Color(0.28, 0.62, 0.28), save_button_height, save_button_font_size)
	_edit_save_btn.pressed.connect(_on_edit_save_pressed)
	_edit_save_btn.disabled = true
	btn_row.add_child(_edit_save_btn)

	_edit_delete_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.item_editor.delete_button"),
		s.button_close, save_button_height, save_button_font_size)
	_edit_delete_btn.pressed.connect(_on_edit_delete_pressed)
	_edit_delete_btn.disabled = true
	btn_row.add_child(_edit_delete_btn)

	return edit


func _build_create_tab() -> Control:
	var s := DevStyle.instance
	var create := VBoxContainer.new()
	create.name = "Create"
	create.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	create.size_flags_vertical = Control.SIZE_EXPAND_FILL
	create.add_theme_constant_override("separation", s.panel_separation)

	var scroll := _scroll_wrap(create)
	_create_form = ItemEditorForm.new()
	scroll.add_child(_create_form)

	_create_error_label = Label.new()
	_create_error_label.add_theme_font_size_override("font_size", s.font_size_small)
	_create_error_label.add_theme_color_override("font_color", Color(1.0, 0.4, 0.4))
	_create_error_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_create_error_label.visible = false
	create.add_child(_create_error_label)

	_create_submit_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.item_editor.create_button"),
		Color(0.28, 0.62, 0.28), save_button_height, save_button_font_size)
	_create_submit_btn.pressed.connect(_on_create_pressed)
	create.add_child(_create_submit_btn)

	_create_form.set_data({}, false)
	return create


func _scroll_wrap(parent: Control) -> MarginContainer:
	var s := DevStyle.instance
	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	parent.add_child(scroll)
	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", s.panel_content_margin)
	margin.add_theme_constant_override("margin_right", s.panel_content_margin)
	margin.add_theme_constant_override("margin_top", s.panel_content_margin)
	margin.add_theme_constant_override("margin_bottom", s.panel_content_margin)
	margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(margin)
	return margin


# ============================================================
# Browse / Edit / Create dispatch
# ============================================================

func _on_browse_item_selected(item_id: int) -> void:
	if _pending_item_id == item_id: return
	_pending_item_id = item_id
	Network.send_dev_item_get(item_id)


func _on_browse_create() -> void:
	_create_form.set_data({}, false)
	_create_error_label.visible = false
	_subtab_container.current_tab = TAB_CREATE


func _on_edit_save_pressed() -> void:
	if _current_item_id == 0: return
	_edit_save_btn.disabled = true
	_edit_save_btn.text = Texts.get_text("ui.dev_hub.item_editor.save_saving")
	var data := _edit_form.get_payload()
	Network.send_dev_item_update(_current_item_id, data)


func _on_edit_delete_pressed() -> void:
	if _current_item_id == 0: return
	var dlg := ConfirmationDialog.new()
	dlg.dialog_text = Texts.get_text("ui.dev_hub.item_editor.delete_confirm")
	dlg.title = Texts.get_text("ui.dev_hub.item_editor.delete_button")
	dlg.ok_button_text = "Delete"
	dlg.confirmed.connect(func():
		Network.send_dev_item_delete(_current_item_id)
		dlg.queue_free())
	dlg.canceled.connect(func(): dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()


func _on_create_pressed() -> void:
	_create_error_label.visible = false
	var data := _create_form.get_payload()
	var slug := str(data.get("slug_id", "")).strip_edges()
	var display := str(data.get("display", "")).strip_edges()
	if slug.is_empty():
		_show_create_error(Texts.get_text("ui.dev_hub.item_editor.error_slug_required"))
		return
	if display.is_empty():
		_show_create_error(Texts.get_text("ui.dev_hub.item_editor.error_display_required"))
		return
	data["slug_id"] = slug
	data["display"] = display
	_create_submit_btn.disabled = true
	Network.send_dev_item_create(data)


func _show_create_error(msg: String) -> void:
	_create_error_label.text = msg
	_create_error_label.visible = true


func _on_edit_form_give_requested(target_username: String, amount: int, is_cert: bool) -> void:
	if _current_item_id == 0: return
	_pending_give_item_id = _current_item_id
	Network.send_dev_item_give(_current_item_id, target_username, amount, is_cert)


func _on_give_result(ok: bool, error_key: String, error_params: Dictionary) -> void:
	if _pending_give_item_id == 0: return
	if _current_item_id != _pending_give_item_id:
		_pending_give_item_id = 0
		return
	_pending_give_item_id = 0
	if _edit_form:
		_edit_form.show_give_result(ok, error_key, error_params)
