extends DevPanel
## LootEditor — two sub-tabs (Items / Credits), each with a paged browse on
## the left and an edit panel on the right. Error messages pop up as modal
## AcceptDialogs (not chat). Create/delete reload the shared server-side
## loot caches so the NPC editor's dropdowns stay in sync.

const TAB_ITEMS := 0
const TAB_CREDITS := 1

@export_group("Browse Columns")
@export var col_width_id: int = 80
@export var col_width_slug: int = 260

var _subtabs: TabContainer
var _items_browse: LootEditorBrowse
var _credits_browse: LootEditorBrowse
var _items_edit: LootEditorItemsEdit
var _credits_edit: LootEditorCreditsEdit

var _items_current_id: int = 0
var _items_pending_id: int = 0
var _credits_current_id: int = 0
var _credits_pending_id: int = 0


func _ready() -> void:
	_build_ui()
	_connect_network()
	_subtabs.current_tab = TAB_ITEMS


func panel_opened() -> void:
	Network.send_dev_loot_list_items(_items_browse.get_search_text(), _items_browse.get_current_page())
	Network.send_dev_loot_list_credits(_credits_browse.get_search_text(), _credits_browse.get_current_page())
	if ItemIcons and not ItemIcons._sync_in_progress:
		ItemIcons.request_sync()


func panel_closed() -> void:
	_flush()


func _connect_network() -> void:
	Network.dev_loot_list_items_result.connect(_on_items_list)
	Network.dev_loot_items_data.connect(_on_items_data)
	Network.dev_loot_items_saved.connect(_on_items_saved)
	Network.dev_loot_created_items.connect(_on_items_created)
	Network.dev_loot_deleted_items.connect(_on_items_deleted)
	Network.dev_loot_list_credits_result.connect(_on_credits_list)
	Network.dev_loot_credits_data.connect(_on_credits_data)
	Network.dev_loot_credits_saved.connect(_on_credits_saved)
	Network.dev_loot_created_credits.connect(_on_credits_created)
	Network.dev_loot_deleted_credits.connect(_on_credits_deleted)
	Network.dev_item_search_result.connect(_on_item_search_result)


# ---------- Items ----------

func _on_items_list(ok: bool, items: Array, total: int, page: int, size: int) -> void:
	if not ok: return
	_items_browse.set_page(items, page, total, size)
	if _items_current_id > 0:
		_items_browse.select_by_id(_items_current_id)


func _on_items_data(ok: bool, data: Dictionary) -> void:
	if _items_pending_id == 0: return
	if not ok:
		_items_pending_id = 0
		_items_edit.clear()
		return
	if int(data.get("id", 0)) != _items_pending_id: return
	_items_current_id = _items_pending_id
	_items_pending_id = 0
	_items_edit.set_data(data)
	_items_browse.select_by_id(_items_current_id)


func _on_items_saved(ok: bool, id: int, slug: String, error: String) -> void:
	if id != _items_current_id:
		_items_edit.set_save_result(ok, false)
		return
	_items_edit.set_save_result(ok, true)
	if ok:
		_items_browse.update_row_slug(id, slug)
	else:
		_show_error(_error_text(error))


func _on_items_browse_selected(id: int) -> void:
	if id <= 0: return
	if id == _items_current_id: return
	_items_pending_id = id
	_items_current_id = 0
	_items_edit.clear()
	_items_edit.set_loading(true)
	Network.send_dev_loot_get_items(id)


func _on_items_page_changed(page: int) -> void:
	Network.send_dev_loot_list_items(_items_browse.get_search_text(), page)


func _on_items_save(items: Array, slug: String) -> void:
	if _items_current_id <= 0: return
	if slug.is_empty():
		_show_error(Texts.get_text("ui.dev_hub.loot_editor.error_slug_required_save"))
		return
	_items_edit.set_save_in_flight()
	Network.send_dev_loot_save_items(_items_current_id, slug, items)


func _on_items_new_requested() -> void:
	_open_new_dialog("items")


func _on_items_delete(id: int, slug: String) -> void:
	_open_delete_dialog("items", id, slug)


func _on_items_created(ok: bool, id: int, error: String) -> void:
	if not ok:
		_show_error(_create_error_text(error))
		return
	Network.send_dev_loot_list_items(_items_browse.get_search_text(), _items_browse.get_current_page())
	_items_pending_id = id
	_items_current_id = 0
	_items_edit.clear()
	_items_edit.set_loading(true)
	Network.send_dev_loot_get_items(id)


func _on_items_deleted(ok: bool, id: int) -> void:
	if not ok:
		_show_error(Texts.get_text("ui.dev_hub.loot_editor.error_delete_failed"))
		return
	if id == _items_current_id:
		_items_current_id = 0
		_items_edit.clear()
	Network.send_dev_loot_list_items(_items_browse.get_search_text(), _items_browse.get_current_page())


# ---------- Credits ----------

func _on_credits_list(ok: bool, items: Array, total: int, page: int, size: int) -> void:
	if not ok: return
	_credits_browse.set_page(items, page, total, size)
	if _credits_current_id > 0:
		_credits_browse.select_by_id(_credits_current_id)


func _on_credits_data(ok: bool, data: Dictionary) -> void:
	if _credits_pending_id == 0: return
	if not ok:
		_credits_pending_id = 0
		_credits_edit.clear()
		return
	if int(data.get("id", 0)) != _credits_pending_id: return
	_credits_current_id = _credits_pending_id
	_credits_pending_id = 0
	_credits_edit.set_data(data)
	_credits_browse.select_by_id(_credits_current_id)


func _on_credits_saved(ok: bool, id: int, slug: String, error: String) -> void:
	if id != _credits_current_id:
		_credits_edit.set_save_result(ok, false)
		return
	_credits_edit.set_save_result(ok, true)
	if ok:
		_credits_browse.update_row_slug(id, slug)
	else:
		_show_error(_error_text(error))


func _on_credits_browse_selected(id: int) -> void:
	if id <= 0: return
	if id == _credits_current_id: return
	_credits_pending_id = id
	_credits_current_id = 0
	_credits_edit.clear()
	_credits_edit.set_loading(true)
	Network.send_dev_loot_get_credits(id)


func _on_credits_page_changed(page: int) -> void:
	Network.send_dev_loot_list_credits(_credits_browse.get_search_text(), page)


func _on_credits_save(min_c: int, max_c: int, slug: String) -> void:
	if _credits_current_id <= 0: return
	if slug.is_empty():
		_show_error(Texts.get_text("ui.dev_hub.loot_editor.error_slug_required_save"))
		return
	if max_c < min_c:
		_show_error(Texts.get_text("ui.dev_hub.loot_editor.error_invalid_range"))
		return
	_credits_edit.set_save_in_flight()
	Network.send_dev_loot_save_credits(_credits_current_id, slug, min_c, max_c)


func _on_credits_new_requested() -> void:
	_open_new_dialog("credits")


func _on_credits_delete(id: int, slug: String) -> void:
	_open_delete_dialog("credits", id, slug)


func _on_credits_created(ok: bool, id: int, error: String) -> void:
	if not ok:
		_show_error(_create_error_text(error))
		return
	Network.send_dev_loot_list_credits(_credits_browse.get_search_text(), _credits_browse.get_current_page())
	_credits_pending_id = id
	_credits_current_id = 0
	_credits_edit.clear()
	_credits_edit.set_loading(true)
	Network.send_dev_loot_get_credits(id)


func _on_credits_deleted(ok: bool, id: int) -> void:
	if not ok:
		_show_error(Texts.get_text("ui.dev_hub.loot_editor.error_delete_failed"))
		return
	if id == _credits_current_id:
		_credits_current_id = 0
		_credits_edit.clear()
	Network.send_dev_loot_list_credits(_credits_browse.get_search_text(), _credits_browse.get_current_page())


# ---------- Autosuggest ----------

func _on_item_search_result(ok: bool, items: Array) -> void:
	if not ok: return
	_items_edit.set_item_search_results(items)


# ---------- Dialogs ----------

func _show_error(text: String) -> void:
	var dlg := AcceptDialog.new()
	dlg.title = Texts.get_text("ui.dev_hub.loot_editor.dialog_error_title")
	dlg.dialog_text = text
	add_child(dlg)
	dlg.popup_centered()
	dlg.confirmed.connect(dlg.queue_free)
	dlg.close_requested.connect(dlg.queue_free)


func _create_error_text(error: String) -> String:
	match error:
		"slug_required": return Texts.get_text("ui.dev_hub.loot_editor.error_slug_required")
		"slug_taken":    return Texts.get_text("ui.dev_hub.loot_editor.error_slug_taken")
		_:               return Texts.get_text("ui.dev_hub.loot_editor.error_create_failed")


func _error_text(error: String) -> String:
	match error:
		"slug_taken":        return Texts.get_text("ui.dev_hub.loot_editor.error_slug_taken_save")
		"not_found":         return Texts.get_text("ui.dev_hub.loot_editor.error_not_found")
		"slug_required":     return Texts.get_text("ui.dev_hub.loot_editor.error_slug_required_save")
		"invalid_range":     return Texts.get_text("ui.dev_hub.loot_editor.error_invalid_range")
		_:                   return Texts.get_text("ui.dev_hub.loot_editor.error_save_failed")


func _open_new_dialog(target: String) -> void:
	var p := "ui.dev_hub.loot_editor."
	var title_key := p + ("new_dialog_title_items" if target == "items" else "new_dialog_title_credits")
	var dlg := ConfirmationDialog.new()
	dlg.title = Texts.get_text(title_key)
	dlg.ok_button_text = Texts.get_text(p + "new_dialog_ok")

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 6)
	dlg.add_child(vbox)

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 6)
	vbox.add_child(row)
	row.add_child(DevFormFields.compact_label(Texts.get_text(p + "new_dialog_slug_label")))
	var slug_edit := LineEdit.new()
	slug_edit.custom_minimum_size = Vector2(200, 0)
	row.add_child(slug_edit)

	dlg.confirmed.connect(func() -> void:
		var slug := slug_edit.text.strip_edges()
		if slug.is_empty():
			_show_error(Texts.get_text(p + "error_slug_required"))
			dlg.queue_free()
			return
		if target == "items":
			Network.send_dev_loot_create_items(slug)
		else:
			Network.send_dev_loot_create_credits(slug)
		dlg.queue_free())
	dlg.canceled.connect(func() -> void: dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()
	slug_edit.grab_focus()


func _open_delete_dialog(target: String, id: int, slug: String) -> void:
	var p := "ui.dev_hub.loot_editor."
	var dlg := ConfirmationDialog.new()
	dlg.title = Texts.get_text(p + "delete_dialog_title")
	dlg.ok_button_text = Texts.get_text(p + "delete_dialog_ok")
	dlg.dialog_text = Texts.get_text(p + "delete_dialog_body", {"slug": slug})
	dlg.confirmed.connect(func() -> void:
		if target == "items":
			Network.send_dev_loot_delete_items(id)
		else:
			Network.send_dev_loot_delete_credits(id)
		dlg.queue_free())
	dlg.canceled.connect(func() -> void: dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()


# ---------- Sub-tab flush ----------

func _on_subtab_changed(tab: int) -> void:
	if tab != TAB_ITEMS:
		_items_current_id = 0
		_items_pending_id = 0
		if is_instance_valid(_items_edit): _items_edit.clear()
	if tab != TAB_CREDITS:
		_credits_current_id = 0
		_credits_pending_id = 0
		if is_instance_valid(_credits_edit): _credits_edit.clear()


func _flush() -> void:
	_items_current_id = 0
	_items_pending_id = 0
	_credits_current_id = 0
	_credits_pending_id = 0
	if is_instance_valid(_items_edit): _items_edit.clear()
	if is_instance_valid(_credits_edit): _credits_edit.clear()


# ---------- UI ----------

func _build_ui() -> void:
	var s := DevStyle.instance
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", s.panel_separation)
	add_child(vbox)

	_subtabs = TabContainer.new()
	_subtabs.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_subtabs.size_flags_vertical = Control.SIZE_EXPAND_FILL
	vbox.add_child(_subtabs)

	var items_tab := HSplitContainer.new()
	items_tab.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	items_tab.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_subtabs.add_child(items_tab)

	_items_browse = LootEditorBrowse.new()
	_items_browse.custom_minimum_size = Vector2(320, 0)
	_items_browse.entry_selected.connect(_on_items_browse_selected)
	_items_browse.page_changed.connect(_on_items_page_changed)
	_items_browse.new_requested.connect(_on_items_new_requested)
	items_tab.add_child(_items_browse)

	_items_edit = LootEditorItemsEdit.new()
	_items_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_items_edit.save_requested.connect(_on_items_save)
	_items_edit.search_requested.connect(func(q: String) -> void: Network.send_dev_item_search(q))
	_items_edit.delete_requested.connect(_on_items_delete)
	items_tab.add_child(_items_edit)

	var credits_tab := HSplitContainer.new()
	credits_tab.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	credits_tab.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_subtabs.add_child(credits_tab)

	_credits_browse = LootEditorBrowse.new()
	_credits_browse.custom_minimum_size = Vector2(320, 0)
	_credits_browse.entry_selected.connect(_on_credits_browse_selected)
	_credits_browse.page_changed.connect(_on_credits_page_changed)
	_credits_browse.new_requested.connect(_on_credits_new_requested)
	credits_tab.add_child(_credits_browse)

	_credits_edit = LootEditorCreditsEdit.new()
	_credits_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_credits_edit.save_requested.connect(_on_credits_save)
	_credits_edit.delete_requested.connect(_on_credits_delete)
	credits_tab.add_child(_credits_edit)

	_items_browse.configure(col_width_id, col_width_slug,
		Texts.get_text("ui.dev_hub.loot_editor.new_button_items"))
	_credits_browse.configure(col_width_id, col_width_slug,
		Texts.get_text("ui.dev_hub.loot_editor.new_button_credits"))

	_subtabs.set_tab_title(TAB_ITEMS, Texts.get_text("ui.dev_hub.loot_editor.tab_items"))
	_subtabs.set_tab_title(TAB_CREDITS, Texts.get_text("ui.dev_hub.loot_editor.tab_credits"))

	_subtabs.tab_changed.connect(_on_subtab_changed)
