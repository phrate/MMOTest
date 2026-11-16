extends DevPanel
## Item editor — three sub-tabs: Browse / Edit / Create.
##
## Cert items are filtered out by the server. Soft delete hides from Browse
## (is_active flag) but existing stacks keep working in-game.
##
## Edit/Create layout (2-column 30/70 grid, each section on a lighter bg):
##   row 0: icon      | identity
##   row 1: blank     | flags
##   row 2: blank     | tags
##   row 3: blank     | capabilities (inner tabs: Equipment | Consumable)
##   row 4: references (full width)
##
## Column widths are tuned via @export vars. Adjust in the scene inspector,
## restart, and reopen the panel to see the change.
##
## Icons live at item_render/icons/<name>.png on the server, shipped through
## ItemIcons autoload. Rendered at native resolution (no scaling).

const TAB_BROWSE := 0
const TAB_EDIT := 1
const TAB_CREATE := 2

const EQUIPMENT_SLOTS := ["head","back","neck","body","belt","legs",
	"wrist_left","wrist_right","hands","ring_1","ring_2","feet",
	"main_hand","off_hand","ammo"]
const ITEM_TYPES := ["weapon","consumable","armor","misc"]
const EFFECT_TYPES := ["heal","buff","utility"]

const FIELD_LABEL_WIDTH := 140
const FIELD_ROW_SEP := 6
const LINE_EDIT_WIDTH := 240
const SPIN_WIDTH := 100
const DROPDOWN_WIDTH := 180
const TEXTAREA_MIN_HEIGHT := 80
const SAVE_BUTTON_HEIGHT := 48
const TAG_CHIP_SEP := 4
const TAG_INPUT_WIDTH := 260
const SECTION_HEADER_HEIGHT := 28
const SECTION_BG := Color(0.16, 0.16, 0.20, 1.0)
const SECTION_PADDING := 10
const CAPABILITY_TAB_PADDING := 10

const ICON_BOX_MIN_SIZE := 120
const ICON_BOX_PADDING := 8

const GOLD := Color(1.0, 0.85, 0.30)
const ACTIVE_COLOR := Color(0.35, 0.20, 0.45, 1.0)
const ACTIVE_BORDER := Color(0.70, 0.40, 0.90, 1.0)

# --- Column widths (pixels). Tags column flexes to fill remaining space. ---
@export_group("Browse Columns")
@export var col_width_slug: int = 160
@export var col_width_display: int = 220
@export var col_width_type: int = 90
@export var col_width_stacks: int = 60
@export var col_width_trade: int = 60
@export var col_width_price: int = 80
@export var col_width_tags_min: int = 100

@export_group("Buttons")
@export var save_button_height: int = 48
@export var save_button_font_size: int = 18

var _current_item_id: int = 0
var _current_data: Dictionary = {}
var _subtab_container: TabContainer
var _style: DevStyle = null

# Browse
var _browse_search: LineEdit
var _browse_tree: Tree
var _browse_create_btn: Button
var _browse_show_inactive: Button
var _item_list: Array = []
var _items_by_id: Dictionary = {}

# Edit
var _edit_form_container: VBoxContainer
var _edit_save_btn: Button
var _edit_delete_btn: Button
var _edit_active_form: Dictionary = {}

# Create
var _create_form_container: VBoxContainer
var _create_submit_btn: Button
var _create_error_label: Label
var _create_active_form: Dictionary = {}

var _pending_item_id: int = 0

# ============================================================
# Lifecycle
# ============================================================

func _ready() -> void:
	_style = DevStyle.instance
	_build_ui()
	_connect_network()
	Network.send_dev_item_list(false)
	_subtab_container.current_tab = TAB_BROWSE
	if ItemIcons:
		ItemIcons.icon_ready.connect(_on_icon_ready)

func panel_opened() -> void:
	Network.send_dev_item_list(_browse_show_inactive.button_pressed)
	if ItemIcons and not ItemIcons._sync_in_progress:
		ItemIcons.request_sync()

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

func _on_icon_ready(_name: String) -> void:
	if _current_item_id != 0 and _subtab_container.current_tab == TAB_EDIT:
		_rebuild_edit_icon_box()

func _on_list_result(ok: bool, items: Array) -> void:
	if not ok: return
	_item_list = items
	_items_by_id.clear()
	_browse_tree.clear()
	var root := _browse_tree.create_item()
	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var iid := int(it.get("id", 0))
		if iid == 0: continue
		_items_by_id[iid] = it
		var row := _browse_tree.create_item(root)
		row.set_text(0, str(it.get("slug_id", "")))
		row.set_text(1, str(it.get("display", "")))
		row.set_text(2, str(it.get("type", "")))
		row.set_text(3, "✔" if bool(it.get("stackable", false)) else "❌")
		row.set_text(4, "✔" if bool(it.get("can_trade", true)) else "❌")
		row.set_text(5, str(int(it.get("base_price", 0))))
		var tags: Array = it.get("tags", [])
		row.set_text(6, ", ".join(tags))
		row.set_metadata(0, iid)
		row.set_text_alignment(0, HORIZONTAL_ALIGNMENT_LEFT)
		row.set_text_alignment(1, HORIZONTAL_ALIGNMENT_LEFT)
		row.set_text_alignment(2, HORIZONTAL_ALIGNMENT_CENTER)
		row.set_text_alignment(3, HORIZONTAL_ALIGNMENT_CENTER)
		row.set_text_alignment(4, HORIZONTAL_ALIGNMENT_CENTER)
		row.set_text_alignment(5, HORIZONTAL_ALIGNMENT_CENTER)
		row.set_text_alignment(6, HORIZONTAL_ALIGNMENT_LEFT)
		row.set_custom_color(0, GOLD)
		if not bool(it.get("is_active", true)):
			for c in 7:
				var col := row.get_custom_color(c)
				row.set_custom_color(c, Color(col.r * 0.6, col.g * 0.6, col.b * 0.6))
	_apply_browse_filter()

func _on_item_data(ok: bool, data: Dictionary) -> void:
	if not ok:
		_pending_item_id = 0
		return
	_current_item_id = int(data.get("id", 0))
	_current_data = data
	_build_edit_form()
	_edit_save_btn.disabled = false
	_edit_delete_btn.disabled = false
	_pending_item_id = 0
	_subtab_container.current_tab = TAB_EDIT

func _on_item_created(ok: bool, item_id: int, error: String) -> void:
	_create_submit_btn.disabled = false
	if ok:
		if _create_error_label: _create_error_label.visible = false
		_pending_item_id = item_id
		Network.send_dev_item_get(item_id)
		Network.send_dev_item_list(_browse_show_inactive.button_pressed)
	else:
		_show_create_error(error if error != "" else Texts.get_text("ui.dev_hub.item_editor.error_create_failed"))

func _on_item_updated(ok: bool, item_id: int) -> void:
	if item_id != _current_item_id: return
	_edit_save_btn.disabled = false
	if ok:
		_edit_save_btn.text = Texts.get_text("ui.dev_hub.item_editor.save_saved")
		Network.send_dev_item_get(item_id)
		Network.send_dev_item_list(_browse_show_inactive.button_pressed)
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
	_current_data = {}
	_subtab_container.current_tab = TAB_BROWSE
	Network.send_dev_item_list(_browse_show_inactive.button_pressed)

# ============================================================
# UI construction
# ============================================================

func _build_ui() -> void:
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.add_theme_constant_override("separation", _style.panel_separation)
	add_child(vbox)

	_subtab_container = TabContainer.new()
	_subtab_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_subtab_container.size_flags_vertical = Control.SIZE_EXPAND_FILL
	vbox.add_child(_subtab_container)

	_build_browse_tab()
	_build_edit_tab()
	_build_create_tab()

	_subtab_container.set_tab_title(TAB_BROWSE, Texts.get_text("ui.dev_hub.item_editor.tab_browse"))
	_subtab_container.set_tab_title(TAB_EDIT,   Texts.get_text("ui.dev_hub.item_editor.tab_edit"))
	_subtab_container.set_tab_title(TAB_CREATE, Texts.get_text("ui.dev_hub.item_editor.tab_create"))

func _build_browse_tab() -> void:
	var browse := VBoxContainer.new()
	browse.name = "Browse"
	browse.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	browse.size_flags_vertical = Control.SIZE_EXPAND_FILL
	browse.add_theme_constant_override("separation", _style.panel_separation)
	_subtab_container.add_child(browse)

	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", _style.panel_separation)
	browse.add_child(top)

	_browse_search = LineEdit.new()
	_browse_search.placeholder_text = Texts.get_text("ui.dev_hub.item_editor.browse_search_placeholder")
	_browse_search.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_browse_search.text_changed.connect(_on_browse_filter_changed)
	top.add_child(_browse_search)

	_browse_show_inactive = Button.new()
	_browse_show_inactive.toggle_mode = true
	_browse_show_inactive.focus_mode = Control.FOCUS_NONE
	_browse_show_inactive.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	_browse_show_inactive.text = Texts.get_text("ui.dev_hub.item_editor.browse_show_inactive")
	_browse_show_inactive.add_theme_font_size_override("font_size", _style.font_size_small)
	_browse_show_inactive.add_theme_color_override("font_color", _style.text_primary)
	_browse_show_inactive.toggled.connect(_on_show_inactive_toggled)
	top.add_child(_browse_show_inactive)

	_browse_create_btn = Button.new()
	_browse_create_btn.text = Texts.get_text("ui.dev_hub.item_editor.browse_create_button")
	_browse_create_btn.focus_mode = Control.FOCUS_NONE
	_browse_create_btn.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	_browse_create_btn.pressed.connect(_on_browse_create_pressed)
	top.add_child(_browse_create_btn)

	_browse_tree = Tree.new()
	_browse_tree.columns = 7
	_browse_tree.column_titles_visible = true
	_browse_tree.set_column_title(0, Texts.get_text("ui.dev_hub.item_editor.browse_col_slug"))
	_browse_tree.set_column_title(1, Texts.get_text("ui.dev_hub.item_editor.browse_col_display"))
	_browse_tree.set_column_title(2, Texts.get_text("ui.dev_hub.item_editor.browse_col_type"))
	_browse_tree.set_column_title(3, Texts.get_text("ui.dev_hub.item_editor.browse_col_stacks"))
	_browse_tree.set_column_title(4, Texts.get_text("ui.dev_hub.item_editor.browse_col_trade"))
	_browse_tree.set_column_title(5, Texts.get_text("ui.dev_hub.item_editor.browse_col_price"))
	_browse_tree.set_column_title(6, Texts.get_text("ui.dev_hub.item_editor.browse_col_tags"))

	var fixed_widths := [col_width_slug, col_width_display, col_width_type,
		col_width_stacks, col_width_trade, col_width_price]
	for i in 6:
		_browse_tree.set_column_expand(i, false)
		_browse_tree.set_column_custom_minimum_width(i, int(fixed_widths[i]))
	_browse_tree.set_column_expand(6, true)
	_browse_tree.set_column_custom_minimum_width(6, col_width_tags_min)

	_browse_tree.set_column_title_alignment(0, HORIZONTAL_ALIGNMENT_LEFT)
	_browse_tree.set_column_title_alignment(1, HORIZONTAL_ALIGNMENT_LEFT)
	_browse_tree.set_column_title_alignment(2, HORIZONTAL_ALIGNMENT_CENTER)
	_browse_tree.set_column_title_alignment(3, HORIZONTAL_ALIGNMENT_CENTER)
	_browse_tree.set_column_title_alignment(4, HORIZONTAL_ALIGNMENT_CENTER)
	_browse_tree.set_column_title_alignment(5, HORIZONTAL_ALIGNMENT_CENTER)
	_browse_tree.set_column_title_alignment(6, HORIZONTAL_ALIGNMENT_LEFT)

	_browse_tree.hide_root = true
	_browse_tree.scroll_horizontal_enabled = false
	_browse_tree.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_browse_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_browse_tree.select_mode = Tree.SELECT_ROW
	_browse_tree.gui_input.connect(_on_browse_tree_input)
	_browse_tree.mouse_exited.connect(_on_browse_tree_mouse_exited)
	browse.add_child(_browse_tree)

func _on_show_inactive_toggled(pressed: bool) -> void:
	_browse_show_inactive.set_meta("is_on", pressed)
	Network.send_dev_item_list(pressed)

func _on_browse_filter_changed(_t: String) -> void:
	_apply_browse_filter()

func _apply_browse_filter() -> void:
	var filter := _browse_search.text.strip_edges().to_lower()
	var root := _browse_tree.get_root()
	if root == null: return
	var child := root.get_first_child()
	while child != null:
		var my_match := filter.is_empty()
		if not my_match:
			if child.get_text(0).to_lower().contains(filter):
				my_match = true
			elif child.get_text(1).to_lower().contains(filter):
				my_match = true
			elif child.get_text(6).to_lower().contains(filter):
				my_match = true
		child.visible = my_match
		child = child.get_next()

func _on_browse_tree_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion:
		var item := _browse_tree.get_item_at_position(_browse_tree.get_local_mouse_position())
		if item != null:
			_browse_tree.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
			if _browse_tree.get_selected() != item:
				_browse_tree.set_selected(item, 0)
		else:
			_browse_tree.mouse_default_cursor_shape = Control.CURSOR_ARROW
	elif event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
			var item := _browse_tree.get_item_at_position(_browse_tree.get_local_mouse_position())
			if item != null:
				_open_item_from_row(item)

func _on_browse_tree_mouse_exited() -> void:
	_browse_tree.mouse_default_cursor_shape = Control.CURSOR_ARROW

func _open_item_from_row(item: TreeItem) -> void:
	var md = item.get_metadata(0)
	if md == null: return
	var iid := int(md)
	if iid == 0: return
	if _pending_item_id == iid: return
	_pending_item_id = iid
	Network.send_dev_item_get(iid)

func _on_browse_create_pressed() -> void:
	_reset_create_state()
	_subtab_container.current_tab = TAB_CREATE

# ============================================================
# Edit tab
# ============================================================

func _build_edit_tab() -> void:
	var edit := VBoxContainer.new()
	edit.name = "Edit"
	edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	edit.size_flags_vertical = Control.SIZE_EXPAND_FILL
	edit.add_theme_constant_override("separation", _style.panel_separation)
	_subtab_container.add_child(edit)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	edit.add_child(scroll)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", _style.panel_content_margin)
	margin.add_theme_constant_override("margin_right", _style.panel_content_margin)
	margin.add_theme_constant_override("margin_top", _style.panel_content_margin)
	margin.add_theme_constant_override("margin_bottom", _style.panel_content_margin)
	margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(margin)

	_edit_form_container = VBoxContainer.new()
	_edit_form_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_edit_form_container.add_theme_constant_override("separation", 8)
	margin.add_child(_edit_form_container)

	var btn_row := HBoxContainer.new()
	btn_row.add_theme_constant_override("separation", _style.panel_separation)
	edit.add_child(btn_row)

	_edit_save_btn = _make_primary_button(Texts.get_text("ui.dev_hub.item_editor.save_button"), Color(0.28, 0.62, 0.28))
	_edit_save_btn.pressed.connect(_on_edit_save_pressed)
	_edit_save_btn.disabled = true
	btn_row.add_child(_edit_save_btn)

	_edit_delete_btn = _make_primary_button(Texts.get_text("ui.dev_hub.item_editor.delete_button"), _style.button_close)
	_edit_delete_btn.pressed.connect(_on_edit_delete_pressed)
	_edit_delete_btn.disabled = true
	btn_row.add_child(_edit_delete_btn)

func _build_create_tab() -> void:
	var create := VBoxContainer.new()
	create.name = "Create"
	create.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	create.size_flags_vertical = Control.SIZE_EXPAND_FILL
	create.add_theme_constant_override("separation", _style.panel_separation)
	_subtab_container.add_child(create)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	create.add_child(scroll)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", _style.panel_content_margin)
	margin.add_theme_constant_override("margin_right", _style.panel_content_margin)
	margin.add_theme_constant_override("margin_top", _style.panel_content_margin)
	margin.add_theme_constant_override("margin_bottom", _style.panel_content_margin)
	margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(margin)

	_create_form_container = VBoxContainer.new()
	_create_form_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_create_form_container.add_theme_constant_override("separation", 8)
	margin.add_child(_create_form_container)

	_create_error_label = Label.new()
	_create_error_label.add_theme_font_size_override("font_size", _style.font_size_small)
	_create_error_label.add_theme_color_override("font_color", Color(1.0, 0.4, 0.4))
	_create_error_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_create_error_label.visible = false
	create.add_child(_create_error_label)

	_create_submit_btn = _make_primary_button(Texts.get_text("ui.dev_hub.item_editor.create_button"), Color(0.28, 0.62, 0.28))
	_create_submit_btn.pressed.connect(_on_create_pressed)
	create.add_child(_create_submit_btn)

	_reset_create_state()

func _make_primary_button(text: String, base: Color) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	b.custom_minimum_size = Vector2(0, save_button_height)
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.add_theme_font_size_override("font_size", save_button_font_size)
	b.add_theme_color_override("font_color", Color.WHITE)
	b.add_theme_color_override("font_hover_color", Color.WHITE)
	b.add_theme_color_override("font_pressed_color", Color.WHITE)
	b.add_theme_color_override("font_disabled_color", Color(0.75, 0.75, 0.75))
	b.add_theme_stylebox_override("normal", _style.make_button_style(base))
	b.add_theme_stylebox_override("hover", _style.make_button_style(base.lightened(0.12)))
	b.add_theme_stylebox_override("pressed", _style.make_button_style(base.darkened(0.12)))
	b.add_theme_stylebox_override("disabled", _style.make_button_style(base.darkened(0.35)))
	return b

# ============================================================
# Section backgrounds
# ============================================================

func _make_section_panel(ratio: float) -> Dictionary:
	var panel := PanelContainer.new()
	panel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	panel.size_flags_stretch_ratio = ratio
	var sb := StyleBoxFlat.new()
	sb.bg_color = SECTION_BG
	sb.corner_radius_top_left = 4
	sb.corner_radius_top_right = 4
	sb.corner_radius_bottom_left = 4
	sb.corner_radius_bottom_right = 4
	sb.content_margin_left = SECTION_PADDING
	sb.content_margin_right = SECTION_PADDING
	sb.content_margin_top = SECTION_PADDING
	sb.content_margin_bottom = SECTION_PADDING
	panel.add_theme_stylebox_override("panel", sb)
	var vbox := VBoxContainer.new()
	vbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vbox.add_theme_constant_override("separation", 4)
	panel.add_child(vbox)
	return {"panel": panel, "vbox": vbox}

func _add_blank_column(parent: HBoxContainer) -> void:
	var blank := Control.new()
	blank.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	blank.size_flags_stretch_ratio = 3.0
	parent.add_child(blank)

func _section_header(key: String) -> Control:
	var box := CenterContainer.new()
	box.custom_minimum_size = Vector2(0, SECTION_HEADER_HEIGHT)
	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.item_editor." + key)
	lbl.add_theme_font_size_override("font_size", _style.font_size_tab)
	lbl.add_theme_color_override("font_color", _style.text_heading)
	box.add_child(lbl)
	return box

# ============================================================
# Form construction
# ============================================================

func _build_edit_form() -> void:
	_edit_active_form = _make_form_state(_current_data, true)
	_render_form(_edit_form_container, _edit_active_form)

func _reset_create_state() -> void:
	_create_active_form = _make_form_state({}, false)
	if _create_form_container:
		_render_form(_create_form_container, _create_active_form)
	if _create_error_label:
		_create_error_label.visible = false

func _make_form_state(src: Dictionary, include_refs: bool) -> Dictionary:
	var tags: Array = []
	var raw_tags: Variant = src.get("tags", [])
	if typeof(raw_tags) == TYPE_ARRAY:
		for t in raw_tags: tags.append(str(t))

	var eq: Variant = src.get("equipment", null)
	var has_eq: bool = eq != null and typeof(eq) == TYPE_DICTIONARY
	var eq_dict: Dictionary = (eq as Dictionary).duplicate() if has_eq else {}

	var cs: Variant = src.get("consumable", null)
	var has_cs: bool = cs != null and typeof(cs) == TYPE_DICTIONARY
	var cs_dict: Dictionary = (cs as Dictionary).duplicate() if has_cs else {}

	return {
		"src": src,
		"fields": {},
		"tags": tags,
		"has_equipment": has_eq,
		"equipment": eq_dict,
		"equipment_widgets": {},
		"has_consumable": has_cs,
		"consumable": cs_dict,
		"consumable_widgets": {},
		"include_references": include_refs,
	}

func _render_form(container: VBoxContainer, form: Dictionary) -> void:
	for c in container.get_children():
		container.remove_child(c)
		c.queue_free()
	form["fields"] = {}
	form["equipment_widgets"] = {}
	form["consumable_widgets"] = {}

	# Row 0: icon | identity
	var row0 := HBoxContainer.new()
	row0.add_theme_constant_override("separation", _style.panel_separation)
	container.add_child(row0)
	_build_icon_section(row0, form)
	_build_identity_section(row0, form)

	# Row 1: blank | flags
	var row1 := HBoxContainer.new()
	row1.add_theme_constant_override("separation", _style.panel_separation)
	container.add_child(row1)
	_add_blank_column(row1)
	_build_flags_section(row1, form)

	# Row 2: blank | tags
	var row2 := HBoxContainer.new()
	row2.add_theme_constant_override("separation", _style.panel_separation)
	container.add_child(row2)
	_add_blank_column(row2)
	_build_tags_section(row2, form)

	# Row 3: blank | capabilities
	var row3 := HBoxContainer.new()
	row3.add_theme_constant_override("separation", _style.panel_separation)
	container.add_child(row3)
	_add_blank_column(row3)
	_build_capabilities_section(row3, form)

	# Row 4: references full width
	if form["include_references"]:
		_build_references_section(container, form)

# --- Icon section ---

func _build_icon_section(parent: HBoxContainer, form: Dictionary) -> void:
	var s := _make_section_panel(3.0)
	var sec: VBoxContainer = s.vbox
	sec.add_child(_section_header("section_icon"))

	var box := PanelContainer.new()
	box.custom_minimum_size = Vector2(ICON_BOX_MIN_SIZE, ICON_BOX_MIN_SIZE)
	box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	box.size_flags_vertical = Control.SIZE_EXPAND_FILL
	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.10, 0.10, 0.14, 1.0)
	sb.border_color = Color(0.35, 0.35, 0.42, 1.0)
	sb.border_width_left = 2
	sb.border_width_right = 2
	sb.border_width_top = 2
	sb.border_width_bottom = 2
	sb.content_margin_left = ICON_BOX_PADDING
	sb.content_margin_right = ICON_BOX_PADDING
	sb.content_margin_top = ICON_BOX_PADDING
	sb.content_margin_bottom = ICON_BOX_PADDING
	box.add_theme_stylebox_override("panel", sb)
	sec.add_child(box)
	form["icon_box"] = box
	_populate_icon_box(box, form)
	parent.add_child(s.panel)

func _populate_icon_box(box: PanelContainer, form: Dictionary) -> void:
	for c in box.get_children():
		box.remove_child(c)
		c.queue_free()

	var icon_name := _resolve_icon_name(form["src"])
	var tex: Texture2D = null
	if not icon_name.is_empty() and ItemIcons:
		tex = ItemIcons.get_texture(icon_name)

	var center := CenterContainer.new()
	center.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	center.size_flags_vertical = Control.SIZE_EXPAND_FILL
	box.add_child(center)

	if tex == null:
		var lbl := Label.new()
		lbl.text = Texts.get_text("ui.dev_hub.item_editor.icon_no_icon")
		lbl.add_theme_font_size_override("font_size", _style.font_size_small)
		lbl.add_theme_color_override("font_color", _style.text_muted)
		lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		center.add_child(lbl)
		return

	var tr := TextureRect.new()
	tr.texture = tex
	tr.custom_minimum_size = tex.get_size()
	tr.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	tr.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	center.add_child(tr)

func _resolve_icon_name(src: Dictionary) -> String:
	var icon_path := str(src.get("icon_path", "")).strip_edges()
	if not icon_path.is_empty(): return icon_path
	var slug := str(src.get("slug_id", "")).strip_edges()
	if not slug.is_empty(): return slug
	return ""

func _rebuild_edit_icon_box() -> void:
	if _edit_active_form.has("icon_box") and is_instance_valid(_edit_active_form["icon_box"]):
		_populate_icon_box(_edit_active_form["icon_box"], _edit_active_form)

# --- Identity section ---

func _build_identity_section(parent: HBoxContainer, form: Dictionary) -> void:
	var s := _make_section_panel(7.0)
	var sec: VBoxContainer = s.vbox
	sec.add_child(_section_header("section_identity"))
	var src: Dictionary = form["src"]
	var fdict: Dictionary = form["fields"]

	_add_line_edit(sec, "label_slug_id", "slug_id", str(src.get("slug_id", "")), not form["include_references"], fdict)
	_add_line_edit(sec, "label_display", "display", str(src.get("display", "")), false, fdict)
	_add_textarea(sec, "label_description", "description", str(src.get("description", "")), fdict)

	var icon_path_value := str(src.get("icon_path", ""))
	var slug_for_placeholder := str(src.get("slug_id", ""))
	var icon_placeholder := ""
	if not slug_for_placeholder.is_empty():
		icon_placeholder = "%s.png (uses slug)" % slug_for_placeholder
	_add_line_edit(sec, "label_icon_path", "icon_path", icon_path_value, false, fdict, icon_placeholder)

	_add_dropdown(sec, "label_type", "type", str(src.get("type", "misc")), ITEM_TYPES, fdict)
	_add_spin(sec, "label_base_price", "base_price", int(src.get("base_price", 0)), 0, 999999999, fdict)

	parent.add_child(s.panel)

# --- Flags section ---

func _build_flags_section(parent: HBoxContainer, form: Dictionary) -> void:
	var s := _make_section_panel(7.0)
	var sec: VBoxContainer = s.vbox
	sec.add_child(_section_header("section_flags"))
	var src: Dictionary = form["src"]
	var fdict: Dictionary = form["fields"]

	var flow := HFlowContainer.new()
	flow.add_theme_constant_override("h_separation", 6)
	flow.add_theme_constant_override("v_separation", 6)
	sec.add_child(flow)

	_add_toggle_to_flow(flow, "label_is_active", "is_active", bool(src.get("is_active", true)), fdict)
	_add_toggle_to_flow(flow, "label_stackable", "stackable", bool(src.get("stackable", false)), fdict)
	_add_toggle_to_flow(flow, "label_can_trade", "can_trade", bool(src.get("can_trade", true)), fdict)
	_add_toggle_to_flow(flow, "label_can_bank", "can_bank", not bool(src.get("non_bankable", false)), fdict)
	_add_toggle_to_flow(flow, "label_can_cert", "can_cert", not bool(src.get("non_certable", false)), fdict)

	parent.add_child(s.panel)

func _add_toggle_to_flow(container: HFlowContainer, label_key: String, save_key: String, pressed: bool, wdict: Dictionary) -> void:
	var btn := _toggle_button(label_key, pressed)
	container.add_child(btn)
	wdict[save_key] = btn

# --- Tags section ---

func _build_tags_section(parent: HBoxContainer, form: Dictionary) -> void:
	var s := _make_section_panel(7.0)
	var sec: VBoxContainer = s.vbox
	sec.add_child(_section_header("section_tags"))
	var chips_box := HFlowContainer.new()
	chips_box.add_theme_constant_override("h_separation", TAG_CHIP_SEP)
	chips_box.add_theme_constant_override("v_separation", TAG_CHIP_SEP)
	sec.add_child(chips_box)
	_render_tags(chips_box, form)

	var input := LineEdit.new()
	input.placeholder_text = Texts.get_text("ui.dev_hub.item_editor.tags_add_placeholder")
	input.custom_minimum_size = Vector2(TAG_INPUT_WIDTH, 0)
	input.text_submitted.connect(_on_tag_input_submit.bind(form, chips_box, input))
	sec.add_child(input)

	parent.add_child(s.panel)

func _render_tags(chips_box: HFlowContainer, form: Dictionary) -> void:
	for c in chips_box.get_children():
		chips_box.remove_child(c)
		c.queue_free()
	var tags: Array = form["tags"]
	for tag in tags:
		_add_tag_chip(chips_box, form, str(tag))

func _add_tag_chip(chips_box: HFlowContainer, form: Dictionary, tag: String) -> void:
	var chip := PanelContainer.new()
	chip.set_meta("tag", tag)
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.20, 0.30, 0.45, 1.0)
	style.border_color = Color(0.40, 0.55, 0.75, 1.0)
	style.border_width_left = 1
	style.border_width_right = 1
	style.border_width_top = 1
	style.border_width_bottom = 1
	style.corner_radius_top_left = 4
	style.corner_radius_top_right = 4
	style.corner_radius_bottom_left = 4
	style.corner_radius_bottom_right = 4
	style.content_margin_left = 12
	style.content_margin_right = 6
	style.content_margin_top = 6
	style.content_margin_bottom = 6
	chip.add_theme_stylebox_override("panel", style)

	var hbox := HBoxContainer.new()
	hbox.add_theme_constant_override("separation", 8)
	chip.add_child(hbox)

	var lbl := Label.new()
	lbl.text = tag
	lbl.add_theme_font_size_override("font_size", _style.font_size_body)
	lbl.add_theme_color_override("font_color", Color.WHITE)
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	hbox.add_child(lbl)

	var x := Button.new()
	x.text = "×"
	x.flat = true
	x.focus_mode = Control.FOCUS_NONE
	x.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	x.add_theme_font_size_override("font_size", _style.font_size_body)
	x.add_theme_color_override("font_color", Color.WHITE)
	x.pressed.connect(_on_tag_remove.bind(form, chips_box, tag))
	hbox.add_child(x)

	chips_box.add_child(chip)

func _on_tag_input_submit(text: String, form: Dictionary, chips_box: HFlowContainer, input: LineEdit) -> void:
	var tag := text.strip_edges()
	if not tag.is_empty():
		var tags: Array = form["tags"]
		if not tags.has(tag):
			tags.append(tag)
			_add_tag_chip(chips_box, form, tag)
	input.clear()
	_synthetic_click(input)

func _synthetic_click(input: LineEdit) -> void:
	# LineEdit can end up in an internal state after text_submitted where it
	# has focus but ignores keystrokes. A real mouse click resets it, so we
	# inject one programmatically.
	var center := input.get_global_rect().get_center()
	var press := InputEventMouseButton.new()
	press.button_index = MOUSE_BUTTON_LEFT
	press.pressed = true
	press.position = center
	press.global_position = center
	Input.parse_input_event(press)
	var release := InputEventMouseButton.new()
	release.button_index = MOUSE_BUTTON_LEFT
	release.pressed = false
	release.position = center
	release.global_position = center
	Input.parse_input_event(release)

func _on_tag_remove(form: Dictionary, chips_box: HFlowContainer, tag: String) -> void:
	var tags: Array = form["tags"]
	tags.erase(tag)
	for child in chips_box.get_children():
		if child.get_meta("tag", "") == tag:
			chips_box.remove_child(child)
			child.queue_free()
			return

# --- Capabilities section (inner tabs) ---

func _build_capabilities_section(parent: HBoxContainer, form: Dictionary) -> void:
	var s := _make_section_panel(7.0)
	var sec: VBoxContainer = s.vbox
	sec.add_child(_section_header("section_capabilities"))

	var tabs := TabContainer.new()
	tabs.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	tabs.size_flags_vertical = Control.SIZE_EXPAND_FILL
	sec.add_child(tabs)

	# --- Equipment tab ---
	var eq_margin := MarginContainer.new()
	eq_margin.add_theme_constant_override("margin_left", CAPABILITY_TAB_PADDING)
	eq_margin.add_theme_constant_override("margin_right", CAPABILITY_TAB_PADDING)
	eq_margin.add_theme_constant_override("margin_top", CAPABILITY_TAB_PADDING)
	eq_margin.add_theme_constant_override("margin_bottom", CAPABILITY_TAB_PADDING)
	tabs.add_child(eq_margin)

	var eq_tab := VBoxContainer.new()
	eq_tab.name = "Equipment"
	eq_tab.add_theme_constant_override("separation", FIELD_ROW_SEP)
	eq_margin.add_child(eq_tab)

	var eq_enable := _toggle_button("enabled", form["has_equipment"])
	eq_enable.toggled.connect(func(p: bool) -> void:
		form["has_equipment"] = p
		_apply_toggle_style(eq_enable, p, "enabled", false)
	)
	eq_tab.add_child(eq_enable)

	_build_equipment_subform(eq_tab, form)

	# --- Consumable tab ---
	var cs_margin := MarginContainer.new()
	cs_margin.add_theme_constant_override("margin_left", CAPABILITY_TAB_PADDING)
	cs_margin.add_theme_constant_override("margin_right", CAPABILITY_TAB_PADDING)
	cs_margin.add_theme_constant_override("margin_top", CAPABILITY_TAB_PADDING)
	cs_margin.add_theme_constant_override("margin_bottom", CAPABILITY_TAB_PADDING)
	tabs.add_child(cs_margin)

	var cs_tab := VBoxContainer.new()
	cs_tab.name = "Consumable"
	cs_tab.add_theme_constant_override("separation", FIELD_ROW_SEP)
	cs_margin.add_child(cs_tab)

	var cs_enable := _toggle_button("enabled", form["has_consumable"])
	cs_enable.toggled.connect(func(p: bool) -> void:
		form["has_consumable"] = p
		_apply_toggle_style(cs_enable, p, "enabled", false)
	)
	cs_tab.add_child(cs_enable)

	_build_consumable_subform(cs_tab, form)

	tabs.set_tab_title(0, Texts.get_text("ui.dev_hub.item_editor.tab_equipment"))
	tabs.set_tab_title(1, Texts.get_text("ui.dev_hub.item_editor.tab_consumable"))

	parent.add_child(s.panel)

func _build_equipment_subform(container: VBoxContainer, form: Dictionary) -> void:
	var eq: Dictionary = form["equipment"]
	var wdict: Dictionary = form["equipment_widgets"]

	_add_dropdown(container, "label_equipment_slot", "slot", str(eq.get("slot", "head")), EQUIPMENT_SLOTS, wdict)
	_add_spin(container, "label_attack_speed_override", "attack_speed_override", int(eq.get("attack_speed_override", 0)), 0, 999, wdict)

	var r1 := _field_row()
	_add_inline_spin(r1, "label_attack_melee", "attack_bonus_melee", int(eq.get("attack_bonus_melee", 0)), wdict)
	_add_inline_spin(r1, "label_attack_ranged", "attack_bonus_ranged", int(eq.get("attack_bonus_ranged", 0)), wdict)
	_add_inline_spin(r1, "label_attack_ether", "attack_bonus_ether", int(eq.get("attack_bonus_ether", 0)), wdict)
	container.add_child(r1)

	var r2 := _field_row()
	_add_inline_spin(r2, "label_defense_melee", "defense_bonus_melee", int(eq.get("defense_bonus_melee", 0)), wdict)
	_add_inline_spin(r2, "label_defense_ranged", "defense_bonus_ranged", int(eq.get("defense_bonus_ranged", 0)), wdict)
	_add_inline_spin(r2, "label_defense_ether", "defense_bonus_ether", int(eq.get("defense_bonus_ether", 0)), wdict)
	container.add_child(r2)

	var r3 := _field_row()
	_add_inline_spin(r3, "label_strength_melee", "bonus_melee_strength", int(eq.get("bonus_melee_strength", 0)), wdict)
	_add_inline_spin(r3, "label_strength_ranged", "bonus_ranged_strength", int(eq.get("bonus_ranged_strength", 0)), wdict)
	_add_inline_spin(r3, "label_strength_ether", "bonus_ether_strength", int(eq.get("bonus_ether_strength", 0)), wdict)
	container.add_child(r3)

func _build_consumable_subform(container: VBoxContainer, form: Dictionary) -> void:
	var cs: Dictionary = form["consumable"]
	var wdict: Dictionary = form["consumable_widgets"]
	_add_dropdown(container, "label_effect_type", "effect_type", str(cs.get("effect_type", "heal")), EFFECT_TYPES, wdict)
	_add_spin(container, "label_heal_amount", "heal_amount", int(cs.get("heal_amount", 0)), 0, 999, wdict)

# --- References section ---

func _build_references_section(container: VBoxContainer, _form: Dictionary) -> void:
	var s := _make_section_panel(1.0)
	var sec: VBoxContainer = s.vbox
	sec.add_child(_section_header("section_references"))
	var refs := Label.new()
	refs.text = Texts.get_text("ui.dev_hub.item_editor.references_none")
	refs.add_theme_font_size_override("font_size", _style.font_size_small)
	refs.add_theme_color_override("font_color", _style.text_muted)
	sec.add_child(refs)
	container.add_child(s.panel)

# ============================================================
# Field widget helpers
# ============================================================

func _field_row() -> HBoxContainer:
	var r := HBoxContainer.new()
	r.add_theme_constant_override("separation", FIELD_ROW_SEP)
	return r

func _inline_label(text: String) -> Label:
	var l := Label.new()
	l.text = text
	l.custom_minimum_size = Vector2(FIELD_LABEL_WIDTH, 0)
	l.clip_text = true
	l.add_theme_font_size_override("font_size", _style.font_size_small)
	l.add_theme_color_override("font_color", _style.text_muted)
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	return l

func _compact_label(text: String) -> Label:
	var l := Label.new()
	l.text = text
	l.add_theme_font_size_override("font_size", _style.font_size_small)
	l.add_theme_color_override("font_color", _style.text_muted)
	l.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	return l

func _small_spin(minv: int, maxv: int, val: int) -> SpinBox:
	var s := SpinBox.new()
	s.min_value = minv
	s.max_value = maxv
	s.step = 1
	s.value = val
	s.custom_minimum_size = Vector2(SPIN_WIDTH, 0)
	s.add_theme_font_size_override("font_size", _style.font_size_small)
	return s

func _add_line_edit(container: VBoxContainer, label_key: String, save_key: String, value: String, readonly: bool, wdict: Dictionary, placeholder: String = "") -> void:
	var row := _field_row()
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.item_editor." + label_key)))
	var le := LineEdit.new()
	le.text = value
	le.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, 0)
	le.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	le.add_theme_font_size_override("font_size", _style.font_size_small)
	if readonly:
		le.editable = false
	if not placeholder.is_empty():
		le.placeholder_text = placeholder
	row.add_child(le)
	container.add_child(row)
	wdict[save_key] = le

func _add_textarea(container: VBoxContainer, label_key: String, save_key: String, value: String, wdict: Dictionary) -> void:
	var row := _field_row()
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.item_editor." + label_key)))
	var te := TextEdit.new()
	te.text = value
	te.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, TEXTAREA_MIN_HEIGHT)
	te.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	te.add_theme_font_size_override("font_size", _style.font_size_small)
	row.add_child(te)
	container.add_child(row)
	wdict[save_key] = te

func _add_spin(container: VBoxContainer, label_key: String, save_key: String, value: int, minv: int, maxv: int, wdict: Dictionary) -> void:
	var row := _field_row()
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.item_editor." + label_key)))
	var sb := _small_spin(minv, maxv, value)
	row.add_child(sb)
	container.add_child(row)
	wdict[save_key] = sb

func _add_inline_spin(row: HBoxContainer, label_key: String, save_key: String, value: int, wdict: Dictionary) -> void:
	row.add_child(_compact_label(Texts.get_text("ui.dev_hub.item_editor." + label_key)))
	var sb := _small_spin(-999999, 999999, value)
	row.add_child(sb)
	wdict[save_key] = sb

func _add_dropdown(container: VBoxContainer, label_key: String, save_key: String, current: String, options: Array, wdict: Dictionary) -> void:
	var row := _field_row()
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.item_editor." + label_key)))
	var dd := OptionButton.new()
	dd.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	dd.add_theme_font_size_override("font_size", _style.font_size_small)
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

func _toggle_button(label_key: String, pressed: bool, active_purple: bool = false) -> Button:
	var btn := Button.new()
	btn.toggle_mode = true
	btn.button_pressed = pressed
	btn.focus_mode = Control.FOCUS_NONE
	btn.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	btn.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	btn.add_theme_font_size_override("font_size", _style.font_size_body)
	btn.add_theme_color_override("font_color", _style.text_primary)
	btn.add_theme_color_override("font_hover_color", _style.text_primary)
	btn.add_theme_color_override("font_pressed_color", Color.WHITE)
	btn.add_theme_color_override("font_hover_pressed_color", Color.WHITE)
	btn.add_theme_color_override("font_disabled_color", _style.text_muted)
	var is_active_key := label_key == "label_is_active" or active_purple
	_apply_toggle_style(btn, pressed, label_key, is_active_key)
	btn.toggled.connect(func(p: bool) -> void: _apply_toggle_style(btn, p, label_key, is_active_key))
	return btn

func _apply_toggle_style(btn: Button, on: bool, label_key: String, is_active: bool) -> void:
	btn.text = Texts.get_text("ui.dev_hub.item_editor." + label_key)
	var sb := StyleBoxFlat.new()
	if on:
		if is_active:
			sb.bg_color = ACTIVE_COLOR
			sb.border_color = ACTIVE_BORDER
		else:
			sb.bg_color = Color(0.18, 0.32, 0.18, 1.0)
			sb.border_color = Color(0.45, 0.85, 0.45, 1.0)
	else:
		sb.bg_color = Color(0.20, 0.20, 0.24, 1.0)
		sb.border_color = Color(0.38, 0.38, 0.44, 1.0)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = 3
	sb.corner_radius_top_right = 3
	sb.corner_radius_bottom_left = 3
	sb.corner_radius_bottom_right = 3
	sb.content_margin_left = 10
	sb.content_margin_right = 10
	sb.content_margin_top = 6
	sb.content_margin_bottom = 6
	btn.add_theme_stylebox_override("normal", sb)
	btn.add_theme_stylebox_override("hover", sb)
	btn.add_theme_stylebox_override("pressed", sb)
	btn.add_theme_stylebox_override("hover_pressed", sb)
	btn.add_theme_stylebox_override("disabled", sb)

# ============================================================
# Payload composition
# ============================================================

func _compose_payload(form: Dictionary, baseline: Dictionary) -> Dictionary:
	var data := baseline.duplicate()

	for key in form["fields"].keys():
		if key == "can_bank" or key == "can_cert":
			continue
		var w = form["fields"][key]
		if w is SpinBox: data[key] = int((w as SpinBox).value)
		elif w is LineEdit: data[key] = (w as LineEdit).text
		elif w is TextEdit: data[key] = (w as TextEdit).text
		elif w is OptionButton:
			var idx: int = (w as OptionButton).selected
			if idx >= 0:
				var md = (w as OptionButton).get_item_metadata(idx)
				data[key] = str(md) if md != null else ""
			else:
				data[key] = ""
		elif w is Button:
			data[key] = (w as Button).button_pressed

	if form["fields"].has("can_bank"):
		data["non_bankable"] = not (form["fields"]["can_bank"] as Button).button_pressed
	if form["fields"].has("can_cert"):
		data["non_certable"] = not (form["fields"]["can_cert"] as Button).button_pressed

	data["tags"] = (form["tags"] as Array).duplicate()

	# Capability values are always sent (whether Enabled or not).
	# Enabled toggle controls whether the capability row exists in DB.
	if form["has_equipment"]:
		var eq := {}
		for key in form["equipment_widgets"].keys():
			var w = form["equipment_widgets"][key]
			if w is SpinBox: eq[key] = int((w as SpinBox).value)
			elif w is OptionButton:
				var idx: int = (w as OptionButton).selected
				eq[key] = str((w as OptionButton).get_item_text(idx)) if idx >= 0 else ""
		data["equipment"] = eq
	else:
		data["equipment"] = null

	if form["has_consumable"]:
		var cs := {}
		for key in form["consumable_widgets"].keys():
			var w = form["consumable_widgets"][key]
			if w is SpinBox: cs[key] = int((w as SpinBox).value)
			elif w is OptionButton:
				var idx: int = (w as OptionButton).selected
				cs[key] = str((w as OptionButton).get_item_text(idx)) if idx >= 0 else ""
		data["consumable"] = cs
	else:
		data["consumable"] = null

	return data

func _on_edit_save_pressed() -> void:
	if _current_item_id == 0: return
	_edit_save_btn.disabled = true
	_edit_save_btn.text = Texts.get_text("ui.dev_hub.item_editor.save_saving")
	var data := _compose_payload(_edit_active_form, _current_data)
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
	if _create_error_label: _create_error_label.visible = false
	var data := _compose_payload(_create_active_form, {})
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
	if _create_error_label == null: return
	_create_error_label.text = msg
	_create_error_label.visible = true
