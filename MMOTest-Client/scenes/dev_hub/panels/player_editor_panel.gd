extends DevPanel
## PlayerEditor — admin tool for inspecting and editing players.
##
## Browse is server-paged (20 per page, 4 fields per row); the full player
## dataset loads on demand when a row is clicked, and is flushed on close,
## sub-tab change, or panel close. No player is ever kept in memory beyond
## the currently-open edit.

const TAB_BROWSE := 0
const TAB_EDIT := 1

@export_group("Browse Columns")
@export var browse_col_width_id: int = 80
@export var browse_col_width_username: int = 220
@export var browse_col_width_final_level: int = 100
@export var browse_col_width_credits: int = 140

@export_group("Layout")
@export var save_button_height: int = 48
@export var save_button_font_size: int = 18
@export var bank_min_height: int = 600

var _subtabs: TabContainer
var _browse: PlayerEditorBrowse
var _form: PlayerEditorForm
var _edit_tab: Control
var _loading_overlay: Control
var _loading_label: Label

var _current_player_id: int = 0
var _pending_player_id: int = 0


func _ready() -> void:
	_build_ui()
	_connect_network()
	_browse.configure(
		browse_col_width_id,
		browse_col_width_username,
		browse_col_width_final_level,
		browse_col_width_credits)
	_subtabs.current_tab = TAB_BROWSE


func panel_opened() -> void:
	Network.send_dev_player_list(_browse.get_search_text(), _browse.get_current_page())


func panel_closed() -> void:
	_flush()


func on_hub_open_for_entity(entity_type: String, entity_id: int) -> void:
	if entity_type != "player": return
	if entity_id <= 0: return
	_pending_player_id = entity_id
	_current_player_id = 0
	_form.clear()
	_subtabs.current_tab = TAB_EDIT
	_show_loading(true)
	Network.send_dev_player_get(entity_id)


# ============================================================
# Network
# ============================================================

func _connect_network() -> void:
	Network.dev_player_list_result.connect(_on_list_result)
	Network.dev_player_data.connect(_on_player_data)
	Network.dev_player_admin_updated.connect(_on_admin_updated)
	Network.dev_player_level_updated.connect(_on_level_updated)
	Network.dev_player_teleport_result.connect(_on_teleport_result)


func _on_list_result(ok: bool, items: Array, total_count: int, page: int, page_size: int) -> void:
	if not ok: return
	_browse.set_page(items, page, total_count, page_size)


func _on_player_data(ok: bool, data: Dictionary) -> void:
	if _pending_player_id == 0:
		return
	if not ok:
		_show_loading(false)
		_pending_player_id = 0
		_subtabs.current_tab = TAB_BROWSE
		if _browse.get_total_count() == 0:
			Network.send_dev_player_list("", 0)
		return
	var got_id := int(data.get("id", 0))
	if got_id != _pending_player_id:
		return
	_current_player_id = got_id
	_pending_player_id = 0
	_form.set_data(data)
	_show_loading(false)


func _on_admin_updated(ok: bool, player_id: int, _is_admin: bool) -> void:
	if not ok: return
	if player_id != _current_player_id: return
	# Optimistic — the toggle already reflects the new value.


func _on_level_updated(ok: bool, player_id: int, skill_id: String,
		new_level: int, current_xp: int) -> void:
	if not ok: return
	if player_id != _current_player_id: return
	_form.update_level_display(skill_id, new_level, current_xp)


func _on_teleport_result(ok: bool, player_id: int, _tx: int, _ty: int, _tz: int,
		error: String) -> void:
	if ok: return
	var msg := ""
	match error:
		"player_offline":
			msg = Texts.get_text("ui.dev_hub.player_editor.teleport_error_player_offline")
		"tile_not_walkable":
			msg = Texts.get_text("ui.dev_hub.player_editor.teleport_error_tile_not_walkable")
		_:
			msg = error
	if not msg.is_empty():
		Network.emit_system_message(msg)


# ============================================================
# Browse → Edit dispatch
# ============================================================

func _on_browse_player_selected(player_id: int) -> void:
	if player_id <= 0: return
	if player_id == _current_player_id and _subtabs.current_tab == TAB_EDIT:
		return
	_pending_player_id = player_id
	_current_player_id = 0
	_form.clear()
	_subtabs.current_tab = TAB_EDIT
	_show_loading(true)
	Network.send_dev_player_get(player_id)


func _on_browse_page_changed(page: int) -> void:
	Network.send_dev_player_list(_browse.get_search_text(), page)


func _on_subtab_changed(tab: int) -> void:
	if tab != TAB_EDIT:
		_flush()


# ============================================================
# Form → Network dispatch
# ============================================================

func _on_form_admin_changed(player_id: int, is_admin: bool) -> void:
	Network.send_dev_player_set_admin(player_id, is_admin)


func _on_form_level_changed(player_id: int, skill_id: String, new_level: int) -> void:
	Network.send_dev_player_update_level(player_id, skill_id, new_level)


func _on_form_teleport_requested(player_id: int, x: int, y: int) -> void:
	Network.send_dev_player_teleport(player_id, x, y)


func _on_form_teleport_to_admin(player_id: int) -> void:
	Network.send_dev_player_teleport_to_admin(player_id)


func _on_form_teleport_admin_to(player_id: int) -> void:
	Network.send_dev_player_teleport_admin_to(player_id)


# ============================================================
# UI
# ============================================================

func _build_ui() -> void:
	var s := DevStyle.instance
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", s.panel_separation)
	add_child(vbox)

	_subtabs = TabContainer.new()
	_subtabs.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_subtabs.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_subtabs.tab_changed.connect(_on_subtab_changed)
	vbox.add_child(_subtabs)

	_browse = PlayerEditorBrowse.new()
	_browse.player_selected.connect(_on_browse_player_selected)
	_browse.page_changed.connect(_on_browse_page_changed)
	_subtabs.add_child(_browse)

	_edit_tab = _build_edit_tab()
	_subtabs.add_child(_edit_tab)

	_subtabs.set_tab_title(TAB_BROWSE, Texts.get_text("ui.dev_hub.player_editor.tab_browse"))
	_subtabs.set_tab_title(TAB_EDIT, Texts.get_text("ui.dev_hub.player_editor.tab_edit"))


func _build_edit_tab() -> Control:
	var s := DevStyle.instance
	var edit := Control.new()
	edit.name = "Edit"
	edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	edit.size_flags_vertical = Control.SIZE_EXPAND_FILL

	var scroll := ScrollContainer.new()
	scroll.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
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

	_form = PlayerEditorForm.new()
	_form.bank_min_height = bank_min_height
	_form.admin_changed.connect(_on_form_admin_changed)
	_form.level_changed.connect(_on_form_level_changed)
	_form.teleport_requested.connect(_on_form_teleport_requested)
	_form.teleport_to_admin_requested.connect(_on_form_teleport_to_admin)
	_form.teleport_admin_to_requested.connect(_on_form_teleport_admin_to)
	margin.add_child(_form)

	# Loading overlay — sits on top of the scroll (Control parent, so anchors
	# are respected; a VBox parent would collapse it to zero size).
	_loading_overlay = Control.new()
	_loading_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_loading_overlay.mouse_filter = Control.MOUSE_FILTER_STOP
	_loading_overlay.visible = false
	edit.add_child(_loading_overlay)

	var dim := ColorRect.new()
	dim.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	dim.color = Color(0, 0, 0, 0.55)
	dim.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_loading_overlay.add_child(dim)

	var center := CenterContainer.new()
	center.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	center.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_loading_overlay.add_child(center)

	_loading_label = Label.new()
	_loading_label.text = Texts.get_text("ui.dev_hub.player_editor.loading_player_data")
	_loading_label.add_theme_font_size_override("font_size", s.font_size_title)
	_loading_label.add_theme_color_override("font_color", s.text_heading)
	center.add_child(_loading_label)

	return edit


func _show_loading(v: bool) -> void:
	if _loading_overlay == null: return
	_loading_overlay.visible = v


func _flush() -> void:
	_current_player_id = 0
	_pending_player_id = 0
	if _form != null:
		_form.clear()
	_show_loading(false)
