extends DevPanel
## NPC editor — four sub-tabs: Browse, Edit, Create, Render.
##
## Browse — table of NPC types, single-click to open, sorted by slug.
## Edit   — spec-driven form for one type (no hitbox/scale editing here).
## Create — same spec, defaults, validation.
## Render — three-column: slug tree (left) | model viewer + mode toggles (middle) | fields (right).
##           Hitbox mode = hitbox gizmo + hitbox fields. Scale mode = scale gizmo + scale fields.
##           Reset Config clears server-side configs for the selected slug.

const TARGET_HEIGHT_BY_SLUG := {
	"player": 1.8, "man": 1.8, "guard": 1.8,
	"goblin": 1.4, "chicken": 0.4,
}

const AI_PACKAGES := ["idle", "wander"]

const TAB_BROWSE := 0
const TAB_EDIT := 1
const TAB_CREATE := 2
const TAB_RENDER := 3

const GRID_COLS := 4
const GRID_CELL_WIDTH := 151
const GRID_LABEL_WIDTH := 85
const GRID_SPIN_WIDTH := 62
const GRID_LABEL_SPIN_SEP := 4
const GRID_CELL_SEP := 12

const LINE_EDIT_WIDTH := 130
const DROPDOWN_WIDTH := 140
const TEXTAREA_MIN_WIDTH := 200
const TEXTAREA_MIN_HEIGHT := 100
const RENDER_SLUG_TREE_MIN_WIDTH := 180
const RENDER_VIEWER_MIN_WIDTH := 280
const RENDER_FIELDS_MIN_WIDTH := 280
const FIELD_LABEL_WIDTH := 90
const FIELD_ROW_SEP := 6
const FIELD_WIDGET_MIN_HEIGHT := 60
const MODE_BUTTON_HEIGHT := 40

const NPC_TYPE_VIEW_SPEC := [
	{"kind": "row", "fields": [
		{"kind": "line_edit", "label": "label_slug_id", "save": "slug_id", "value_key": "slug_id"},
		{"kind": "line_edit", "label": "label_display", "save": "display", "value_key": "display"},
	]},
	{"kind": "row", "fields": [
		{"kind": "dropdown", "label": "label_render", "save": "render_slug", "value_key": "render_slug", "options_key": "render_slugs"},
		{"kind": "dropdown", "label": "label_dialogue_script", "save": "dialogue_script", "value_key": "dialogue_script", "options_key": "dialogue_scripts", "prepend": ["_none"], "label_to_value": {"_none": ""}},
	]},
	{"kind": "separator"},
	{"kind": "row", "fields": [
		{"kind": "dropdown", "label": "label_loot_table", "save": "loot_table_name", "value_key": "loot_table_name", "options_key": "loot_lists"},
		{"kind": "dropdown", "label": "label_credits_table", "save": "credits_table_name", "value_key": "credits_table_name", "options_key": "credits_loot_lists"},
	]},
	{"kind": "row", "fields": [
		{"kind": "dropdown", "label": "label_ai_package", "save": "ai_package_state", "value_key": "ai_package_state", "options_static": ["idle", "wander"]},
	]},
	{"kind": "row", "fields": [
		{"kind": "textarea", "label": "label_spawn_flags", "save": "spawn_flags", "value_key": "spawn_flags"},
	]},
	{"kind": "separator"},
	{"kind": "section", "key": "section_features"},
	{"kind": "row", "fields": [
		{"kind": "toggle", "label": "label_is_banker", "save": "is_banker", "value_key": "is_banker"},
		{"kind": "toggle", "label": "label_attackable", "save": "attackable", "value_key": "attackable"},
		{"kind": "toggle", "label": "label_aggressive", "save": "aggressive", "value_key": "aggressive"},
	]},
	{"kind": "separator"},
	{"kind": "section", "key": "section_combat"},
	{"kind": "combat_grid"},
]

@export var model_viewport_min_size: Vector2 = Vector2(280, 360)
@export var default_orbit_yaw: float = 0.6
@export var default_orbit_pitch: float = 0.35
@export var orbit_mouse_sensitivity: float = 0.008
@export var orbit_zoom_step: float = 0.9
@export var orbit_min_distance: float = 0.5
@export var orbit_max_distance: float = 50.0
@export var orbit_pitch_clamp: float = 1.4
@export var left_column_ratio: float = 0.25
@export var save_all_button_height: int = 32
@export var hitbox_capsule_alpha: float = 0.375

var _current_type_id: int = 0
var _current_data: Dictionary = {}

var _subtab_container: TabContainer

# Browse
var _browse_tree: Tree
var _browse_search: LineEdit
var _browse_create_btn: Button

# Edit tab
var _edit_fields_container: VBoxContainer
var _edit_save_button: Button
var _edit_delete_button: Button
var _edit_fields: Dictionary = {}

var _sub_viewport: SubViewport
var _sub_viewport_container: SubViewportContainer
var _camera: Camera3D
var _model_holder: Node3D
var _current_model: Node3D = null
var _current_slug: String = ""

var _orbit_yaw: float = 0.6
var _orbit_pitch: float = 0.35
var _orbit_distance: float = 4.0
var _orbit_target: Vector3 = Vector3(0, 1, 0)
var _orbiting: bool = false

# Create tab
var _create_fields_container: VBoxContainer
var _create_submit_button: Button
var _create_error_label: Label
var _create_fields: Dictionary = {}

# Render tab
var _render_slug_search: LineEdit
var _render_slug_tree: Tree
var _render_hitbox_btn: Button
var _render_scale_btn: Button
var _render_sub_viewport: SubViewport
var _render_sub_viewport_container: SubViewportContainer
var _render_camera: Camera3D
var _render_model_holder: Node3D
var _render_model: Node3D = null
var _render_selected_slug: String = ""
var _render_view_mode: String = "hitbox"
var _render_fields_container: VBoxContainer
var _render_gizmo: HitboxGizmo = null
var _render_scale_gizmo: ScaleGizmo = null

var _render_orbit_yaw: float = 0.6
var _render_orbit_pitch: float = 0.35
var _render_orbit_distance: float = 4.0
var _render_orbit_target: Vector3 = Vector3(0, 1, 0)
var _render_orbiting: bool = false

# Cached values used by scale gizmo — computed once at model load
var _render_base_fit: float = 1.0
var _render_bounds_at_scale_one: Vector3 = Vector3.ZERO

# Hitbox/scale editing state
var _hitbox: Dictionary = {}
var _hitbox_dirty: bool = false
var _user_scale: Vector3 = Vector3.ONE
var _scale_dirty: bool = false

var _hitbox_type_selector: OptionButton
var _hitbox_radius_spin: SpinBox
var _hitbox_height_spin: SpinBox
var _hitbox_offset_x_spin: SpinBox
var _hitbox_offset_y_spin: SpinBox
var _hitbox_offset_z_spin: SpinBox

var _scale_x_spin: SpinBox
var _scale_y_spin: SpinBox
var _scale_z_spin: SpinBox

# Options
var _options: Dictionary = {}
var _options_loaded: bool = false

# Type list
var _type_list: Array = []
var _types_by_id: Dictionary = {}
var _list_loaded: bool = false
var _pending_type_id: int = 0

var _style: DevStyle = null

# ============================================================
# Lifecycle
# ============================================================

func _ready() -> void:
	_style = DevStyle.instance
	_build_ui()
	_connect_network()
	_orbit_yaw = default_orbit_yaw
	_orbit_pitch = default_orbit_pitch
	_render_orbit_yaw = default_orbit_yaw
	_render_orbit_pitch = default_orbit_pitch
	_request_options()
	_request_type_list()
	_subtab_container.current_tab = TAB_BROWSE

func panel_opened() -> void:
	if not _options_loaded:
		_request_options()
	if not _list_loaded:
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
	Network.npc_render_save_result.connect(_on_npc_render_save_result)
	Network.dev_npc_render_config_reset.connect(_on_render_config_reset)

func _request_options() -> void:
	Network.send_dev_npc_editor_options_request()

func _on_options_result(ok: bool, options: Dictionary) -> void:
	if not ok:
		push_warning("[NpcEditor] Options request failed")
		return
	_options = options
	_options_loaded = true
	print("[NpcEditor] Options loaded: render_slugs=%d dialogue=%d loot=%d credits=%d" % [
		(_options.get("render_slugs", []) as Array).size(),
		(_options.get("dialogue_scripts", []) as Array).size(),
		(_options.get("loot_lists", []) as Array).size(),
		(_options.get("credits_loot_lists", []) as Array).size(),
	])
	_rebuild_render_slug_tree()
	if _current_type_id != 0:
		_rebuild_edit_panel()
	if _subtab_container and _subtab_container.current_tab == TAB_CREATE:
		_build_create_form()

func _request_type_list() -> void:
	Network.send_dev_npc_type_list()

func _on_type_list_result(ok: bool, items: Array) -> void:
	if not ok: return
	_type_list = items
	_types_by_id.clear()
	_browse_tree.clear()
	var root := _browse_tree.create_item()
	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var tid := int(it.get("id", 0))
		if tid == 0: continue
		_types_by_id[tid] = it
		var row := _browse_tree.create_item(root)
		row.set_text(0, str(it.get("slug_id", "")))
		row.set_text(1, str(it.get("display", "")))
		row.set_text(2, str(it.get("render_slug", "")))
		row.set_text(3, str(int(it.get("instance_count", 0))))
		row.set_metadata(0, tid)
	_list_loaded = true
	_apply_browse_filter()
	_rebuild_render_slug_tree()

func _on_type_data(ok: bool, data: Dictionary) -> void:
	if not ok:
		_pending_type_id = 0
		return
	_current_type_id = int(data.get("id", 0))
	_current_data = data
	_load_edit_model(str(data.get("render_slug", "")))
	_rebuild_edit_panel()
	_edit_save_button.disabled = false
	_edit_delete_button.disabled = false
	_pending_type_id = 0
	_subtab_container.current_tab = TAB_EDIT

func _on_type_created(ok: bool, type_id: int, error: String) -> void:
	_create_submit_button.disabled = false
	if ok:
		if _create_error_label:
			_create_error_label.visible = false
		_pending_type_id = type_id
		Network.send_dev_npc_type_get(type_id)
	else:
		_show_create_error(error if error != "" else Texts.get_text("ui.dev_hub.npc_editor.error_create_failed"))

func _on_type_updated(ok: bool, type_id: int) -> void:
	if type_id != _current_type_id: return
	_edit_save_button.disabled = false
	if ok:
		_edit_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_saved")
		Network.send_dev_npc_type_get(type_id)
		await get_tree().create_timer(1.2).timeout
		if _edit_save_button and _edit_save_button.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_saved"):
			_edit_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")
	else:
		_edit_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_failed")
		await get_tree().create_timer(2.0).timeout
		if _edit_save_button and _edit_save_button.text == Texts.get_text("ui.dev_hub.npc_editor.save_all_failed"):
			_edit_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")

func _on_type_deleted(ok: bool, type_id: int) -> void:
	if type_id != _current_type_id: return
	if not ok: return
	_current_type_id = 0
	_current_data = {}
	_clear_edit_model()
	_subtab_container.current_tab = TAB_BROWSE
	_request_type_list()

func _on_npc_render_save_result(ok: bool, kind: String, slug: String) -> void:
	if slug != _render_selected_slug: return
	if not ok:
		push_warning("[NpcEditor] Save %s failed for slug %s" % [kind, slug])

func _on_render_config_reset(ok: bool, slug: String) -> void:
	if not ok: return
	if slug != _render_selected_slug: return
	_load_render_model(_render_selected_slug)
	_rebuild_render_fields()

# ============================================================
# Top-level UI
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
	_build_render_tab()

	_subtab_container.set_tab_title(TAB_BROWSE, Texts.get_text("ui.dev_hub.npc_editor.tab_browse"))
	_subtab_container.set_tab_title(TAB_EDIT,   Texts.get_text("ui.dev_hub.npc_editor.tab_edit"))
	_subtab_container.set_tab_title(TAB_CREATE, Texts.get_text("ui.dev_hub.npc_editor.tab_create"))
	_subtab_container.set_tab_title(TAB_RENDER, Texts.get_text("ui.dev_hub.npc_editor.tab_render"))
	_subtab_container.tab_changed.connect(_on_subtab_changed)

func _on_subtab_changed(tab: int) -> void:
	if tab == TAB_BROWSE:
		_request_type_list()
	elif tab == TAB_EDIT:
		if _current_type_id == 0:
			_edit_save_button.disabled = true
			_edit_delete_button.disabled = true
	elif tab == TAB_CREATE:
		_build_create_form()
	elif tab == TAB_RENDER:
		if not _options_loaded:
			_request_options()
		_rebuild_render_slug_tree()

# ============================================================
# Browse tab
# ============================================================

func _build_browse_tab() -> void:
	var browse := VBoxContainer.new()
	browse.name = "Browse"
	browse.add_theme_constant_override("separation", _style.panel_separation)
	_subtab_container.add_child(browse)

	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", _style.panel_separation)
	browse.add_child(top)

	_browse_search = LineEdit.new()
	_browse_search.placeholder_text = Texts.get_text("ui.dev_hub.npc_editor.browse_search_placeholder")
	_browse_search.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_browse_search.text_changed.connect(_on_browse_filter_changed)
	top.add_child(_browse_search)

	_browse_create_btn = Button.new()
	_browse_create_btn.text = Texts.get_text("ui.dev_hub.npc_editor.browse_create_button")
	_browse_create_btn.focus_mode = Control.FOCUS_NONE
	_browse_create_btn.pressed.connect(_on_browse_create_pressed)
	top.add_child(_browse_create_btn)

	_browse_tree = Tree.new()
	_browse_tree.columns = 4
	_browse_tree.column_titles_visible = true
	_browse_tree.set_column_title(0, Texts.get_text("ui.dev_hub.npc_editor.browse_col_slug"))
	_browse_tree.set_column_title(1, Texts.get_text("ui.dev_hub.npc_editor.browse_col_display"))
	_browse_tree.set_column_title(2, Texts.get_text("ui.dev_hub.npc_editor.browse_col_render"))
	_browse_tree.set_column_title(3, Texts.get_text("ui.dev_hub.npc_editor.browse_col_instances"))
	_browse_tree.hide_root = true
	_browse_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_browse_tree.select_mode = Tree.SELECT_ROW
	_browse_tree.mouse_filter = Control.MOUSE_FILTER_STOP
	_browse_tree.gui_input.connect(_on_browse_tree_input)
	_browse_tree.mouse_exited.connect(_on_browse_tree_mouse_exited)
	_browse_tree.item_activated.connect(_on_browse_tree_activated)
	browse.add_child(_browse_tree)

func _on_browse_create_pressed() -> void:
	_subtab_container.current_tab = TAB_CREATE

func _on_browse_filter_changed(_t: String) -> void:
	_apply_browse_filter()

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
				_open_type_from_tree_item(item)

func _on_browse_tree_mouse_exited() -> void:
	_browse_tree.mouse_default_cursor_shape = Control.CURSOR_ARROW

func _on_browse_tree_activated() -> void:
	var selected := _browse_tree.get_selected()
	if selected != null:
		_open_type_from_tree_item(selected)

func _open_type_from_tree_item(item: TreeItem) -> void:
	var md = item.get_metadata(0)
	if md == null: return
	var tid := int(md)
	if tid == 0: return
	if _pending_type_id == tid: return
	_pending_type_id = tid
	Network.send_dev_npc_type_get(tid)

func _apply_browse_filter() -> void:
	var filter := _browse_search.text.strip_edges().to_lower()
	var root := _browse_tree.get_root()
	if root == null: return
	var child := root.get_first_child()
	while child != null:
		var my_match := filter.is_empty()
		if not my_match:
			for c in 3:
				if child.get_text(c).to_lower().contains(filter):
					my_match = true
					break
		child.visible = my_match
		child = child.get_next()

# ============================================================
# Edit tab
# ============================================================

func _build_edit_tab() -> void:
	var edit := VBoxContainer.new()
	edit.name = "Edit"
	edit.add_theme_constant_override("separation", _style.panel_separation)
	_subtab_container.add_child(edit)

	var split := HSplitContainer.new()
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	edit.add_child(split)

	var left := VBoxContainer.new()
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	left.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.add_child(left)

	_sub_viewport_container = SubViewportContainer.new()
	_sub_viewport_container.custom_minimum_size = model_viewport_min_size
	_sub_viewport_container.stretch = true
	_sub_viewport_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_sub_viewport_container.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_sub_viewport_container.gui_input.connect(_on_edit_viewport_input)
	left.add_child(_sub_viewport_container)

	_sub_viewport = SubViewport.new()
	_sub_viewport.own_world_3d = true
	_sub_viewport.handle_input_locally = false
	_sub_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_sub_viewport_container.add_child(_sub_viewport)

	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.15, 0.15, 0.18, 1.0)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(1, 1, 1)
	env.ambient_light_energy = 0.5
	var world_env := WorldEnvironment.new()
	world_env.environment = env
	_sub_viewport.add_child(world_env)

	_camera = Camera3D.new()
	_camera.current = true
	_sub_viewport.add_child(_camera)

	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-50, -30, 0)
	light.light_energy = 1.2
	light.shadow_enabled = true
	_sub_viewport.add_child(light)

	_model_holder = Node3D.new()
	_model_holder.name = "ModelHolder"
	_sub_viewport.add_child(_model_holder)

	var right_margin := MarginContainer.new()
	right_margin.add_theme_constant_override("margin_left", _style.panel_separation)
	right_margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right_margin.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right_margin.clip_contents = true
	split.add_child(right_margin)

	var right := VBoxContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right.add_theme_constant_override("separation", _style.panel_separation)
	right.clip_contents = true
	right_margin.add_child(right)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	right.add_child(scroll)

	_edit_fields_container = VBoxContainer.new()
	_edit_fields_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_edit_fields_container.add_theme_constant_override("separation", 4)
	scroll.add_child(_edit_fields_container)

	var buttons_row := HBoxContainer.new()
	buttons_row.add_theme_constant_override("separation", _style.panel_separation)
	right.add_child(buttons_row)

	_edit_save_button = Button.new()
	_edit_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")
	_edit_save_button.disabled = true
	_edit_save_button.focus_mode = Control.FOCUS_NONE
	_edit_save_button.custom_minimum_size = Vector2(0, save_all_button_height)
	_edit_save_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_edit_save_button.add_theme_font_size_override("font_size", _style.font_size_body)
	_edit_save_button.add_theme_color_override("font_color", _style.text_primary)
	_edit_save_button.add_theme_color_override("font_hover_color", _style.text_primary)
	_edit_save_button.add_theme_color_override("font_pressed_color", _style.text_primary)
	_edit_save_button.add_theme_color_override("font_disabled_color", _style.text_muted)
	_edit_save_button.pressed.connect(_on_edit_save_pressed)
	buttons_row.add_child(_edit_save_button)

	_edit_delete_button = Button.new()
	_edit_delete_button.text = Texts.get_text("ui.dev_hub.npc_editor.delete_button")
	_edit_delete_button.disabled = true
	_edit_delete_button.focus_mode = Control.FOCUS_NONE
	_edit_delete_button.custom_minimum_size = Vector2(0, save_all_button_height)
	_edit_delete_button.add_theme_font_size_override("font_size", _style.font_size_body)
	_edit_delete_button.add_theme_color_override("font_color", _style.text_primary)
	_edit_delete_button.add_theme_color_override("font_hover_color", _style.text_primary)
	_edit_delete_button.add_theme_color_override("font_pressed_color", _style.text_primary)
	_edit_delete_button.add_theme_stylebox_override("normal", _style.make_button_style(_style.button_close))
	_edit_delete_button.add_theme_stylebox_override("hover", _style.make_button_style(_style.button_close.lightened(0.12)))
	_edit_delete_button.add_theme_stylebox_override("pressed", _style.make_button_style(_style.button_close.darkened(0.12)))
	_edit_delete_button.pressed.connect(_on_edit_delete_pressed)
	buttons_row.add_child(_edit_delete_button)

func _rebuild_edit_panel() -> void:
	for c in _edit_fields_container.get_children():
		_edit_fields_container.remove_child(c)
		c.queue_free()
	_edit_fields.clear()
	_build_view_from_spec(NPC_TYPE_VIEW_SPEC, _edit_fields_container, _edit_fields)
	_edit_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all")

func _on_edit_save_pressed() -> void:
	if _current_type_id == 0: return
	_edit_save_button.disabled = true
	_edit_save_button.text = Texts.get_text("ui.dev_hub.npc_editor.save_all_saving")
	var data := _current_data.duplicate()
	for key in _edit_fields.keys():
		var c = _edit_fields[key]
		if c is SpinBox: data[key] = int((c as SpinBox).value)
		elif c is LineEdit: data[key] = (c as LineEdit).text
		elif c is TextEdit: data[key] = (c as TextEdit).text
		elif c is OptionButton:
			var idx: int = (c as OptionButton).selected
			if idx >= 0:
				var md = (c as OptionButton).get_item_metadata(idx)
				data[key] = str(md) if md != null else ""
			else:
				data[key] = ""
		elif c is Button:
			data[key] = (c as Button).button_pressed
	Network.send_dev_npc_type_update(_current_type_id, data)

func _on_edit_delete_pressed() -> void:
	if _current_type_id == 0: return
	var dlg := ConfirmationDialog.new()
	dlg.dialog_text = Texts.get_text("ui.dev_hub.npc_editor.delete_confirm")
	dlg.title = Texts.get_text("ui.dev_hub.npc_editor.delete_button")
	dlg.ok_button_text = "Delete"
	dlg.add_cancel_button("Cancel")
	dlg.confirmed.connect(func():
		Network.send_dev_npc_type_delete(_current_type_id)
		dlg.queue_free())
	dlg.canceled.connect(func(): dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()

# ============================================================
# Create tab
# ============================================================

func _build_create_tab() -> void:
	var create := VBoxContainer.new()
	create.name = "Create"
	create.add_theme_constant_override("separation", _style.panel_separation)
	_subtab_container.add_child(create)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	create.add_child(scroll)

	var inner := MarginContainer.new()
	inner.add_theme_constant_override("margin_left", _style.panel_content_margin)
	inner.add_theme_constant_override("margin_right", _style.panel_content_margin)
	inner.add_theme_constant_override("margin_top", _style.panel_content_margin)
	inner.add_theme_constant_override("margin_bottom", _style.panel_content_margin)
	inner.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(inner)

	_create_fields_container = VBoxContainer.new()
	_create_fields_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_create_fields_container.add_theme_constant_override("separation", 4)
	inner.add_child(_create_fields_container)

	var btn_row := HBoxContainer.new()
	btn_row.add_theme_constant_override("separation", _style.panel_separation)
	create.add_child(btn_row)

	_create_submit_button = Button.new()
	_create_submit_button.text = Texts.get_text("ui.dev_hub.npc_editor.create_button")
	_create_submit_button.focus_mode = Control.FOCUS_NONE
	_create_submit_button.custom_minimum_size = Vector2(0, save_all_button_height)
	_create_submit_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_create_submit_button.add_theme_font_size_override("font_size", _style.font_size_body)
	_create_submit_button.add_theme_color_override("font_color", _style.text_primary)
	_create_submit_button.add_theme_color_override("font_hover_color", _style.text_primary)
	_create_submit_button.add_theme_color_override("font_pressed_color", _style.text_primary)
	_create_submit_button.pressed.connect(_on_create_submit_pressed)
	btn_row.add_child(_create_submit_button)

	_create_error_label = Label.new()
	_create_error_label.add_theme_font_size_override("font_size", _style.font_size_small)
	_create_error_label.add_theme_color_override("font_color", Color(1.0, 0.4, 0.4))
	_create_error_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_create_error_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_create_error_label.visible = false
	create.add_child(_create_error_label)

func _build_create_form() -> void:
	if not _options_loaded:
		_request_options()
	for c in _create_fields_container.get_children():
		_create_fields_container.remove_child(c)
		c.queue_free()
	_create_fields.clear()
	if _create_error_label:
		_create_error_label.visible = false
	var saved := _current_data
	_current_data = {"loot_table_name": "__nothing"}
	_build_view_from_spec(NPC_TYPE_VIEW_SPEC, _create_fields_container, _create_fields)
	_current_data = saved
	if _create_fields.has("slug_id"):
		var le = _create_fields["slug_id"]
		if le is LineEdit:
			le.editable = true

func _on_create_submit_pressed() -> void:
	if _create_error_label:
		_create_error_label.visible = false
	var data := {}
	for key in _create_fields.keys():
		var c = _create_fields[key]
		if c is SpinBox: data[key] = int((c as SpinBox).value)
		elif c is LineEdit: data[key] = (c as LineEdit).text
		elif c is TextEdit: data[key] = (c as TextEdit).text
		elif c is OptionButton:
			var idx: int = (c as OptionButton).selected
			if idx >= 0:
				var md = (c as OptionButton).get_item_metadata(idx)
				data[key] = str(md) if md != null else ""
			else:
				data[key] = ""
		elif c is Button:
			data[key] = (c as Button).button_pressed
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
	_create_submit_button.disabled = true
	Network.send_dev_npc_type_create(data)

func _show_create_error(msg: String) -> void:
	if _create_error_label == null: return
	_create_error_label.text = msg
	_create_error_label.visible = true

# ============================================================
# Render tab
# ============================================================

func _build_render_tab() -> void:
	var render := VBoxContainer.new()
	render.name = "Render"
	render.add_theme_constant_override("separation", _style.panel_separation)
	_subtab_container.add_child(render)

	var split_outer := HSplitContainer.new()
	split_outer.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split_outer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	render.add_child(split_outer)

	# --- Left: slug search + tree ---
	var left := VBoxContainer.new()
	left.custom_minimum_size = Vector2(RENDER_SLUG_TREE_MIN_WIDTH, 0)
	left.add_theme_constant_override("separation", _style.panel_separation)
	split_outer.add_child(left)

	var left_header := Label.new()
	left_header.text = "Render Slugs"
	left_header.add_theme_font_size_override("font_size", _style.font_size_tab)
	left_header.add_theme_color_override("font_color", _style.text_heading)
	left.add_child(left_header)

	_render_slug_search = LineEdit.new()
	_render_slug_search.placeholder_text = Texts.get_text("ui.dev_hub.npc_editor.render_slug_search_placeholder")
	_render_slug_search.text_changed.connect(_on_render_slug_search_changed)
	left.add_child(_render_slug_search)

	_render_slug_tree = Tree.new()
	_render_slug_tree.columns = 2
	_render_slug_tree.column_titles_visible = true
	_render_slug_tree.set_column_title(0, "Slug")
	_render_slug_tree.set_column_title(1, "Types")
	_render_slug_tree.hide_root = true
	_render_slug_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_render_slug_tree.select_mode = Tree.SELECT_ROW
	_render_slug_tree.item_activated.connect(_on_render_slug_activated)
	_render_slug_tree.item_selected.connect(_on_render_slug_activated)
	left.add_child(_render_slug_tree)

	# --- Right of outer: middle (viewer) | right (fields) ---
	var split_inner := HSplitContainer.new()
	split_inner.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	split_inner.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split_outer.add_child(split_inner)

	# --- Middle: mode buttons + viewer ---
	var middle := VBoxContainer.new()
	middle.custom_minimum_size = Vector2(RENDER_VIEWER_MIN_WIDTH, 0)
	middle.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	middle.size_flags_vertical = Control.SIZE_EXPAND_FILL
	middle.add_theme_constant_override("separation", _style.panel_separation)
	split_inner.add_child(middle)

	var mode_row := HBoxContainer.new()
	mode_row.add_theme_constant_override("separation", 4)
	middle.add_child(mode_row)

	_render_hitbox_btn = _make_view_toggle(Texts.get_text("ui.dev_hub.npc_editor.view_hitbox"), "hitbox")
	mode_row.add_child(_render_hitbox_btn)

	_render_scale_btn = _make_view_toggle(Texts.get_text("ui.dev_hub.npc_editor.view_scale"), "scale")
	mode_row.add_child(_render_scale_btn)

	_sync_render_view_toggles()

	_render_sub_viewport_container = SubViewportContainer.new()
	_render_sub_viewport_container.custom_minimum_size = Vector2(RENDER_VIEWER_MIN_WIDTH, 280)
	_render_sub_viewport_container.stretch = true
	_render_sub_viewport_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_render_sub_viewport_container.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_render_sub_viewport_container.gui_input.connect(_on_render_viewport_input)
	middle.add_child(_render_sub_viewport_container)

	_render_sub_viewport = SubViewport.new()
	_render_sub_viewport.own_world_3d = true
	_render_sub_viewport.handle_input_locally = false
	_render_sub_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_render_sub_viewport_container.add_child(_render_sub_viewport)

	var renv := Environment.new()
	renv.background_mode = Environment.BG_COLOR
	renv.background_color = Color(0.15, 0.15, 0.18, 1.0)
	renv.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	renv.ambient_light_color = Color(1, 1, 1)
	renv.ambient_light_energy = 0.5
	var rworld_env := WorldEnvironment.new()
	rworld_env.environment = renv
	_render_sub_viewport.add_child(rworld_env)

	_render_camera = Camera3D.new()
	_render_camera.current = true
	_render_sub_viewport.add_child(_render_camera)

	var rlight := DirectionalLight3D.new()
	rlight.rotation_degrees = Vector3(-50, -30, 0)
	rlight.light_energy = 1.2
	rlight.shadow_enabled = true
	_render_sub_viewport.add_child(rlight)

	_render_model_holder = Node3D.new()
	_render_model_holder.name = "RenderModelHolder"
	_render_sub_viewport.add_child(_render_model_holder)

	_render_gizmo = HitboxGizmo.new()
	_render_gizmo.camera = _render_camera
	_render_gizmo.hitbox_capsule_alpha = hitbox_capsule_alpha
	_render_sub_viewport.add_child(_render_gizmo)
	_render_gizmo.hitbox_changed.connect(_on_gizmo_hitbox_changed)
	_render_gizmo.set_gizmo_visible(false)

	_render_scale_gizmo = ScaleGizmo.new()
	_render_scale_gizmo.camera = _render_camera
	_render_sub_viewport.add_child(_render_scale_gizmo)
	_render_scale_gizmo.scale_changed.connect(_on_scale_gizmo_changed)
	_render_scale_gizmo.set_gizmo_visible(false)

	# --- Right: fields + reset ---
	var right_margin := MarginContainer.new()
	right_margin.add_theme_constant_override("margin_left", _style.panel_separation)
	right_margin.custom_minimum_size = Vector2(RENDER_FIELDS_MIN_WIDTH, 0)
	right_margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right_margin.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right_margin.clip_contents = true
	split_inner.add_child(right_margin)

	var right := VBoxContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right.add_theme_constant_override("separation", _style.panel_separation)
	right.clip_contents = true
	right_margin.add_child(right)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.clip_contents = true
	right.add_child(scroll)

	_render_fields_container = VBoxContainer.new()
	_render_fields_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_render_fields_container.add_theme_constant_override("separation", 4)
	scroll.add_child(_render_fields_container)

	var bottom_row := HBoxContainer.new()
	bottom_row.add_theme_constant_override("separation", _style.panel_separation)
	right.add_child(bottom_row)

	var green := Color(0.28, 0.62, 0.28)

	var save_config_btn := Button.new()
	save_config_btn.text = "Save Config"
	save_config_btn.focus_mode = Control.FOCUS_NONE
	save_config_btn.custom_minimum_size = Vector2(0, 48)
	save_config_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	save_config_btn.add_theme_font_size_override("font_size", 18)
	save_config_btn.add_theme_color_override("font_color", Color.WHITE)
	save_config_btn.add_theme_color_override("font_hover_color", Color.WHITE)
	save_config_btn.add_theme_color_override("font_pressed_color", Color.WHITE)
	save_config_btn.add_theme_stylebox_override("normal", _style.make_button_style(green))
	save_config_btn.add_theme_stylebox_override("hover", _style.make_button_style(green.lightened(0.12)))
	save_config_btn.add_theme_stylebox_override("pressed", _style.make_button_style(green.darkened(0.12)))
	save_config_btn.pressed.connect(_on_save_config_pressed)
	bottom_row.add_child(save_config_btn)

	var reset_config_btn := Button.new()
	reset_config_btn.text = "Reset Config"
	reset_config_btn.focus_mode = Control.FOCUS_NONE
	reset_config_btn.custom_minimum_size = Vector2(0, 48)
	reset_config_btn.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	reset_config_btn.add_theme_font_size_override("font_size", 18)
	reset_config_btn.add_theme_color_override("font_color", Color.WHITE)
	reset_config_btn.add_theme_color_override("font_hover_color", Color.WHITE)
	reset_config_btn.add_theme_color_override("font_pressed_color", Color.WHITE)
	reset_config_btn.add_theme_stylebox_override("normal", _style.make_button_style(_style.button_close))
	reset_config_btn.add_theme_stylebox_override("hover", _style.make_button_style(_style.button_close.lightened(0.12)))
	reset_config_btn.add_theme_stylebox_override("pressed", _style.make_button_style(_style.button_close.darkened(0.12)))
	reset_config_btn.pressed.connect(_on_render_reset_config_pressed)
	bottom_row.add_child(reset_config_btn)

	_rebuild_render_slug_tree()

func _make_view_toggle(text: String, view_id: String) -> Button:
	var b := Button.new()
	b.text = text
	b.toggle_mode = true
	b.focus_mode = Control.FOCUS_NONE
	b.custom_minimum_size = Vector2(0, MODE_BUTTON_HEIGHT)
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.alignment = HORIZONTAL_ALIGNMENT_CENTER
	b.add_theme_font_size_override("font_size", _style.font_size_body)
	b.add_theme_color_override("font_color", _style.text_primary)
	b.add_theme_color_override("font_hover_color", _style.text_primary)
	b.add_theme_color_override("font_pressed_color", _style.text_heading)
	b.add_theme_color_override("font_hover_pressed_color", _style.text_heading)
	b.pressed.connect(_on_render_view_toggle.bind(view_id))
	return b

func _on_render_view_toggle(view_id: String) -> void:
	_render_view_mode = view_id
	_sync_render_view_toggles()
	_rebuild_render_fields()
	_update_render_gizmo_visibility()

func _sync_render_view_toggles() -> void:
	if _render_hitbox_btn: _render_hitbox_btn.set_pressed_no_signal(_render_view_mode == "hitbox")
	if _render_scale_btn: _render_scale_btn.set_pressed_no_signal(_render_view_mode == "scale")

func _on_render_slug_search_changed(_t: String) -> void:
	_apply_render_slug_filter()

func _apply_render_slug_filter() -> void:
	if _render_slug_tree == null or _render_slug_search == null: return
	var filter := _render_slug_search.text.strip_edges().to_lower()
	var root := _render_slug_tree.get_root()
	if root == null: return
	var child := root.get_first_child()
	while child != null:
		var slug := str(child.get_metadata(0))
		child.visible = filter.is_empty() or slug.to_lower().contains(filter)
		child = child.get_next()

func _rebuild_render_slug_tree() -> void:
	if _render_slug_tree == null: return
	_render_slug_tree.clear()
	var root := _render_slug_tree.create_item()
	var slugs: Array = (_options.get("render_slugs", []) as Array).duplicate()
	slugs.sort()
	for slug_v in slugs:
		var slug := str(slug_v)
		var count := 0
		for t in _type_list:
			if typeof(t) == TYPE_DICTIONARY and str(t.get("render_slug", "")) == slug:
				count += 1
		var row := _render_slug_tree.create_item(root)
		row.set_text(0, slug)
		row.set_text(1, str(count))
		row.set_metadata(0, slug)
		if slug == _render_selected_slug:
			row.select(0)
	_apply_render_slug_filter()

func _on_render_slug_activated() -> void:
	var sel := _render_slug_tree.get_selected()
	if sel == null: return
	var md = sel.get_metadata(0)
	if md == null: return
	var slug := str(md)
	if slug.is_empty() or slug == _render_selected_slug: return
	_render_selected_slug = slug
	_load_render_model(slug)
	_rebuild_render_fields()

func _load_render_model(slug: String) -> void:
	if _render_model != null and is_instance_valid(_render_model):
		_render_model.queue_free()
	_render_model = null
	_render_bounds_at_scale_one = Vector3.ZERO
	_render_base_fit = 1.0
	if slug.is_empty(): return
	var scene := NpcRender.get_scene(slug)
	if scene == null: return
	var inst := scene.instantiate() as Node3D
	if inst == null: return
	_render_model_holder.add_child(inst)
	_render_model = inst

	# base_fit computed on fresh model (before any scale applied)
	_render_base_fit = _compute_base_fit(inst, slug)

	# Load user scale from cache and apply
	_user_scale = _load_scale_for_slug(slug)
	_apply_model_scale(inst, _render_base_fit, _user_scale)

	# Compute bounds at scale=1 (base AABB * base_fit), constant across scale changes
	var aabb := _measure_world_aabb(inst)
	if _user_scale.x > 0.001 and _user_scale.y > 0.001 and _user_scale.z > 0.001:
		_render_bounds_at_scale_one = Vector3(
			aabb.size.x / _user_scale.x,
			aabb.size.y / _user_scale.y,
			aabb.size.z / _user_scale.z
		)

	_frame_viewer_camera(inst, _render_camera, _render_orbit_yaw, _render_orbit_pitch, 1.0)
	_render_orbit_target = _measure_world_aabb(inst).get_center()
	_render_orbit_distance = (_render_camera.global_position - _render_orbit_target).length()

func _rebuild_render_fields() -> void:
	for c in _render_fields_container.get_children():
		_render_fields_container.remove_child(c)
		c.queue_free()
	if _render_selected_slug.is_empty(): return

	if _render_view_mode == "hitbox":
		_hitbox = _load_hitbox_for_slug(_render_selected_slug)
		_hitbox_dirty = false
		_build_hitbox_view(_render_fields_container)
		if _render_gizmo != null:
			_render_gizmo.set_hitbox(_hitbox)
	else:
		_user_scale = _load_scale_for_slug(_render_selected_slug)
		_scale_dirty = false
		_build_scale_view(_render_fields_container)
		if _render_scale_gizmo != null and _render_model != null:
			var aabb := _measure_world_aabb(_render_model)
			_render_scale_gizmo.set_scale_and_bounds(_user_scale, _render_bounds_at_scale_one)
			_render_scale_gizmo.position = aabb.get_center()

	_update_render_gizmo_visibility()

func _update_render_gizmo_visibility() -> void:
	if _render_gizmo == null or _render_scale_gizmo == null: return
	var has_slug := not _render_selected_slug.is_empty()
	_render_gizmo.set_gizmo_visible(has_slug and _render_view_mode == "hitbox")
	_render_scale_gizmo.set_gizmo_visible(has_slug and _render_view_mode == "scale")

func _on_render_viewport_input(event: InputEvent) -> void:
	if _render_view_mode == "hitbox" and _render_gizmo != null:
		if event is InputEventMouseButton or event is InputEventMouseMotion:
			if _render_gizmo.handle_input(event, event.position):
				get_viewport().set_input_as_handled()
				return
	elif _render_view_mode == "scale" and _render_scale_gizmo != null:
		if event is InputEventMouseButton or event is InputEventMouseMotion:
			if _render_scale_gizmo.handle_input(event, event.position):
				get_viewport().set_input_as_handled()
				return

	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_render_orbiting = mb.pressed
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_zoom_render_camera(orbit_zoom_step)
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_zoom_render_camera(1.0 / orbit_zoom_step)
			get_viewport().set_input_as_handled()
			return
	elif event is InputEventMouseMotion and _render_orbiting:
		var mm := event as InputEventMouseMotion
		_render_orbit_yaw -= mm.relative.x * orbit_mouse_sensitivity
		_render_orbit_pitch = clampf(_render_orbit_pitch + mm.relative.y * orbit_mouse_sensitivity, -orbit_pitch_clamp, orbit_pitch_clamp)
		_update_render_camera_orbit()

func _zoom_render_camera(factor: float) -> void:
	# factor > 1 = zoom out (distance grows), factor < 1 = zoom in (distance shrinks)
	var offset := _render_camera.global_position - _render_orbit_target
	var new_dist := clampf(offset.length() * factor, orbit_min_distance, orbit_max_distance)
	_render_orbit_distance = new_dist
	_update_render_camera_orbit()

func _update_render_camera_orbit() -> void:
	if _render_camera == null: return
	var cp := cos(_render_orbit_pitch)
	var sp := sin(_render_orbit_pitch)
	var cy := cos(_render_orbit_yaw)
	var sy := sin(_render_orbit_yaw)
	var offset := Vector3(_render_orbit_distance * cp * sy, _render_orbit_distance * sp, _render_orbit_distance * cp * cy)
	_render_camera.global_position = _render_orbit_target + offset
	_render_camera.look_at(_render_orbit_target, Vector3.UP)

func _on_render_reset_config_pressed() -> void:
	if _render_selected_slug.is_empty(): return
	Network.send_npc_render_reset_config(_render_selected_slug)

func _on_save_config_pressed() -> void:
	if _render_selected_slug.is_empty(): return

	# Save whichever config was just edited from memory; pull the other from
	# cache so we don't overwrite it with stale in-memory state from a
	# previous slug or previous mode.
	var hitbox_to_save := _hitbox
	var scale_to_save := _user_scale
	if _render_view_mode != "hitbox":
		hitbox_to_save = _load_hitbox_for_slug(_render_selected_slug)
	if _render_view_mode != "scale":
		scale_to_save = _load_scale_for_slug(_render_selected_slug)

	_write_hitbox_to_cache(_render_selected_slug, hitbox_to_save)
	Network.send_npc_render_save_hitbox(_render_selected_slug, hitbox_to_save.duplicate())

	var render := {"scale_x": scale_to_save.x, "scale_y": scale_to_save.y, "scale_z": scale_to_save.z}
	_write_render_to_cache(_render_selected_slug, render)
	Network.send_npc_render_save_render(_render_selected_slug, render)

	_hitbox_dirty = false
	_scale_dirty = false
# ============================================================
# Hitbox editor (Render tab, hitbox mode)
# ============================================================

func _build_hitbox_view(container: VBoxContainer) -> void:
	var is_capsule := str(_hitbox.get("type", "capsule")) == "capsule"

	var r_type := _field_row()
	_hitbox_type_selector = OptionButton.new()
	_hitbox_type_selector.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_hitbox_type_selector.add_item(Texts.get_text("ui.dev_hub.npc_editor.hitbox_type_sphere"), 0)
	_hitbox_type_selector.add_item(Texts.get_text("ui.dev_hub.npc_editor.hitbox_type_capsule"), 1)
	_hitbox_type_selector.select(1 if is_capsule else 0)
	_hitbox_type_selector.item_selected.connect(_on_hitbox_type_selected)
	_add_widget_with_label(r_type, Texts.get_text("ui.dev_hub.npc_editor.hitbox_type"), _hitbox_type_selector)
	container.add_child(r_type)

	var r_rad := _field_row()
	_hitbox_radius_spin = _inline_spin(0.05, 10.0, 0.05, float(_hitbox.get("radius", 0.4)))
	_hitbox_radius_spin.value_changed.connect(_on_hitbox_radius_changed)
	_add_widget_with_label(r_rad, Texts.get_text("ui.dev_hub.npc_editor.hitbox_radius"), _hitbox_radius_spin)
	container.add_child(r_rad)

	if is_capsule:
		var r_h := _field_row()
		_hitbox_height_spin = _inline_spin(0.1, 10.0, 0.05, float(_hitbox.get("height", 1.8)))
		_hitbox_height_spin.value_changed.connect(_on_hitbox_height_changed)
		_add_widget_with_label(r_h, Texts.get_text("ui.dev_hub.npc_editor.hitbox_height"), _hitbox_height_spin)
		container.add_child(r_h)
	else:
		_hitbox_height_spin = null

	var r_off := _field_row()
	_hitbox_offset_x_spin = _inline_spin(-10.0, 10.0, 0.05, float(_hitbox.get("offset_x", 0.0)))
	_hitbox_offset_x_spin.value_changed.connect(_on_hitbox_offset_changed)
	_add_widget_with_label(r_off, Texts.get_text("ui.dev_hub.npc_editor.hitbox_offset_x"), _hitbox_offset_x_spin)

	_hitbox_offset_y_spin = _inline_spin(-10.0, 10.0, 0.05, float(_hitbox.get("offset_y", 0.9)))
	_hitbox_offset_y_spin.value_changed.connect(_on_hitbox_offset_changed)
	_add_widget_with_label(r_off, Texts.get_text("ui.dev_hub.npc_editor.hitbox_offset_y"), _hitbox_offset_y_spin)

	_hitbox_offset_z_spin = _inline_spin(-10.0, 10.0, 0.05, float(_hitbox.get("offset_z", 0.0)))
	_hitbox_offset_z_spin.value_changed.connect(_on_hitbox_offset_changed)
	_add_widget_with_label(r_off, Texts.get_text("ui.dev_hub.npc_editor.hitbox_offset_z"), _hitbox_offset_z_spin)
	container.add_child(r_off)

	if _render_gizmo != null:
		_render_gizmo.set_hitbox(_hitbox)

func _load_hitbox_for_slug(slug: String) -> Dictionary:
	var hb := NpcRender.get_hitbox(slug)
	if not hb.is_empty():
		return {
			"type": str(hb.get("type", "capsule")),
			"radius": float(hb.get("radius", 0.4)),
			"height": float(hb.get("height", 1.8)),
			"offset_x": float(hb.get("offset_x", 0.0)),
			"offset_y": float(hb.get("offset_y", 0.9)),
			"offset_z": float(hb.get("offset_z", 0.0)),
		}
	return _default_hitbox_for_model()

func _default_hitbox_for_model() -> Dictionary:
	if _render_model == null:
		return {"type": "capsule", "radius": 0.4, "height": 1.8,
				"offset_x": 0.0, "offset_y": 0.9, "offset_z": 0.0}
	var aabb := _measure_world_aabb(_render_model)
	if aabb.size == Vector3.ZERO:
		return {"type": "capsule", "radius": 0.4, "height": 1.8,
				"offset_x": 0.0, "offset_y": 0.9, "offset_z": 0.0}
	var center := aabb.get_center()
	var radius := maxf(maxf(aabb.size.x, aabb.size.z) * 0.5, 0.1)
	var height := maxf(aabb.size.y, radius * 2.0 + 0.1)
	return {"type": "capsule", "radius": radius, "height": height,
			"offset_x": center.x, "offset_y": center.y, "offset_z": center.z}

func _on_gizmo_hitbox_changed(new_config: Dictionary) -> void:
	_hitbox = new_config.duplicate()
	if _hitbox_radius_spin: _hitbox_radius_spin.set_value_no_signal(float(_hitbox.get("radius", 0.4)))
	if _hitbox_height_spin: _hitbox_height_spin.set_value_no_signal(float(_hitbox.get("height", 1.8)))
	if _hitbox_offset_x_spin: _hitbox_offset_x_spin.set_value_no_signal(float(_hitbox.get("offset_x", 0.0)))
	if _hitbox_offset_y_spin: _hitbox_offset_y_spin.set_value_no_signal(float(_hitbox.get("offset_y", 0.9)))
	if _hitbox_offset_z_spin: _hitbox_offset_z_spin.set_value_no_signal(float(_hitbox.get("offset_z", 0.0)))
	_mark_hitbox_dirty()

func _on_hitbox_type_selected(idx: int) -> void:
	_hitbox["type"] = "capsule" if idx == 1 else "sphere"
	if _render_gizmo != null: _render_gizmo.set_hitbox(_hitbox)
	_mark_hitbox_dirty()
	_rebuild_render_fields()

func _on_hitbox_radius_changed(v: float) -> void:
	_hitbox["radius"] = v
	if str(_hitbox.get("type", "capsule")) == "capsule" and float(_hitbox.get("height", 1.8)) < v * 2.0 + 0.05:
		_hitbox["height"] = v * 2.0 + 0.05
		if _hitbox_height_spin: _hitbox_height_spin.set_value_no_signal(_hitbox["height"])
	if _render_gizmo != null: _render_gizmo.set_hitbox(_hitbox)
	_mark_hitbox_dirty()

func _on_hitbox_height_changed(v: float) -> void:
	_hitbox["height"] = v
	if _render_gizmo != null: _render_gizmo.set_hitbox(_hitbox)
	_mark_hitbox_dirty()

func _on_hitbox_offset_changed(_v: float) -> void:
	_hitbox["offset_x"] = _hitbox_offset_x_spin.value
	_hitbox["offset_y"] = _hitbox_offset_y_spin.value
	_hitbox["offset_z"] = _hitbox_offset_z_spin.value
	if _render_gizmo != null: _render_gizmo.set_hitbox(_hitbox)
	_mark_hitbox_dirty()

func _mark_hitbox_dirty() -> void:
	_hitbox_dirty = true


# ============================================================
# Scale editor (Render tab, scale mode)
# ============================================================

func _build_scale_view(container: VBoxContainer) -> void:
	var r1 := _field_row()
	_scale_x_spin = _inline_spin(0.1, 5.0, 0.05, _user_scale.x)
	_scale_x_spin.value_changed.connect(_on_scale_changed)
	_add_widget_with_label(r1, Texts.get_text("ui.dev_hub.npc_editor.scale_x"), _scale_x_spin)

	_scale_y_spin = _inline_spin(0.1, 5.0, 0.05, _user_scale.y)
	_scale_y_spin.value_changed.connect(_on_scale_changed)
	_add_widget_with_label(r1, Texts.get_text("ui.dev_hub.npc_editor.scale_y"), _scale_y_spin)

	_scale_z_spin = _inline_spin(0.1, 5.0, 0.05, _user_scale.z)
	_scale_z_spin.value_changed.connect(_on_scale_changed)
	_add_widget_with_label(r1, Texts.get_text("ui.dev_hub.npc_editor.scale_z"), _scale_z_spin)
	container.add_child(r1)

func _load_scale_for_slug(slug: String) -> Vector3:
	var rc := NpcRender.get_render_config(slug)
	if rc.is_empty(): return Vector3.ONE
	return Vector3(
		float(rc.get("scale_x", 1.0)),
		float(rc.get("scale_y", 1.0)),
		float(rc.get("scale_z", 1.0)))

func _mark_scale_dirty() -> void:
	_scale_dirty = true


func _apply_current_scale_and_sync_gizmo() -> void:
	if _render_model == null or not is_instance_valid(_render_model): return
	_apply_model_scale(_render_model, _render_base_fit, _user_scale)
	if _render_scale_gizmo != null:
		var aabb := _measure_world_aabb(_render_model)
		_render_scale_gizmo.set_scale_and_bounds(_user_scale, _render_bounds_at_scale_one)
		_render_scale_gizmo.position = aabb.get_center()
		# Don't move the camera during a gizmo drag — the drag plane is
		# anchored in world space and moving the camera corrupts the delta.
		if not _render_scale_gizmo.is_dragging():
			_render_orbit_target = aabb.get_center()
			_update_render_camera_orbit()

func _on_scale_gizmo_changed(new_scale: Vector3) -> void:
	_user_scale = new_scale
	if _scale_x_spin: _scale_x_spin.set_value_no_signal(_user_scale.x)
	if _scale_y_spin: _scale_y_spin.set_value_no_signal(_user_scale.y)
	if _scale_z_spin: _scale_z_spin.set_value_no_signal(_user_scale.z)
	_apply_current_scale_and_sync_gizmo()
	_mark_scale_dirty()

func _on_scale_changed(_v: float) -> void:
	_user_scale = Vector3(_scale_x_spin.value, _scale_y_spin.value, _scale_z_spin.value)
	_apply_current_scale_and_sync_gizmo()
	_mark_scale_dirty()

# ============================================================
# Spec renderer
# ============================================================

func _build_view_from_spec(spec: Array, container: VBoxContainer, field_dict: Dictionary) -> void:
	for item in spec:
		var k := str(item.get("kind", ""))
		if k == "row":
			var r := _field_row()
			for f in item["fields"]:
				_add_field_widget(r, f, field_dict)
			container.add_child(r)
		elif k == "section":
			container.add_child(_section_header(str(item["key"])))
		elif k == "separator":
			container.add_child(HSeparator.new())
		elif k == "combat_grid":
			_build_combat_stats_grid(container, field_dict)

func _add_field_widget(row: HBoxContainer, f: Dictionary, field_dict: Dictionary) -> void:
	var kind := str(f["kind"])
	var value_key := str(f.get("value_key", ""))
	var value: Variant = _current_data.get(value_key) if value_key != "" else null

	if kind == "line_edit":
		_add_line_edit_to_row(row, str(f["label"]), str(f["save"]),
			str(value) if value != null else "",
			bool(f.get("readonly", false)), field_dict)
	elif kind == "spin":
		_add_spin_to_row(row, str(f["label"]), str(f["save"]),
			int(value) if value != null else 0,
			int(f.get("min", 0)), int(f.get("max", 999999)), field_dict)
	elif kind == "toggle":
		_add_checkbox_to_row(row, str(f["label"]), str(f["save"]),
			bool(value) if value != null else false, field_dict)
	elif kind == "textarea":
		_add_textarea_to_row(row, str(f["label"]), str(f["save"]),
			str(value) if value != null else "", field_dict)
	elif kind == "dropdown":
		var options: Array = []
		if f.has("options_static"):
			options = (f["options_static"] as Array).duplicate()
		elif f.has("options_key"):
			var raw = _options.get(str(f["options_key"]), [])
			if raw is Array:
				options = (raw as Array).duplicate()
		if f.has("prepend"):
			options = (f["prepend"] as Array) + options
		var label_to_value: Dictionary = f.get("label_to_value", {})
		_add_dropdown_to_row(row, str(f["label"]), str(f["save"]),
			str(value) if value != null else "",
			options, label_to_value, field_dict)

# ============================================================
# Widget builders
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

func _inline_spin(minv: float, maxv: float, step: float, val: float) -> SpinBox:
	var s := SpinBox.new()
	s.min_value = minv
	s.max_value = maxv
	s.step = step
	s.value = val
	s.custom_minimum_size = Vector2(72, 0)
	s.add_theme_font_size_override("font_size", _style.font_size_small)
	return s

func _small_button(text: String) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	b.add_theme_font_size_override("font_size", _style.font_size_small)
	b.add_theme_color_override("font_color", _style.text_primary)
	b.add_theme_color_override("font_hover_color", _style.text_primary)
	b.add_theme_color_override("font_pressed_color", _style.text_primary)
	return b

func _section_header(key: String) -> Control:
	var box := CenterContainer.new()
	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.npc_editor." + key)
	lbl.add_theme_font_size_override("font_size", _style.font_size_tab)
	lbl.add_theme_color_override("font_color", _style.text_heading)
	box.add_child(lbl)
	return box

func _add_widget_with_label(row: HBoxContainer, label_text: String, widget: Control) -> void:
	row.add_child(_compact_label(label_text))
	row.add_child(widget)

func _add_line_edit_to_row(row: HBoxContainer, label_key: String, save_key: String, value: String, readonly: bool, field_dict: Dictionary) -> void:
	var le := LineEdit.new()
	le.text = value
	le.custom_minimum_size = Vector2(LINE_EDIT_WIDTH, 0)
	le.add_theme_font_size_override("font_size", _style.font_size_small)
	if readonly:
		le.editable = false
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.npc_editor." + label_key)))
	row.add_child(le)
	field_dict[save_key] = le

func _add_spin_to_row(row: HBoxContainer, label_key: String, save_key: String, value: int, minv: int, maxv: int, field_dict: Dictionary) -> void:
	row.add_child(_compact_label(Texts.get_text("ui.dev_hub.npc_editor." + label_key)))
	var sb := _inline_spin(minv, maxv, 1, value)
	row.add_child(sb)
	field_dict[save_key] = sb

func _add_checkbox_to_row(row: HBoxContainer, label_key: String, save_key: String, pressed: bool, field_dict: Dictionary) -> void:
	var btn := Button.new()
	btn.toggle_mode = true
	btn.button_pressed = pressed
	btn.focus_mode = Control.FOCUS_NONE
	btn.add_theme_font_size_override("font_size", _style.font_size_body)
	btn.add_theme_color_override("font_color", _style.text_primary)
	btn.add_theme_color_override("font_hover_color", _style.text_primary)
	btn.add_theme_color_override("font_pressed_color", Color(0.75, 1.0, 0.75))
	btn.add_theme_color_override("font_hover_pressed_color", Color(0.9, 1.0, 0.9))
	btn.add_theme_color_override("font_disabled_color", _style.text_muted)
	_apply_toggle_style(btn, pressed, label_key)
	btn.toggled.connect(func(p: bool) -> void: _apply_toggle_style(btn, p, label_key))
	row.add_child(btn)
	field_dict[save_key] = btn

func _apply_toggle_style(btn: Button, on: bool, label_key: String) -> void:
	var base_text := Texts.get_text("ui.dev_hub.npc_editor." + label_key)
	btn.text = ("[x] " if on else "[ ] ") + base_text
	var sb := StyleBoxFlat.new()
	if on:
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
	sb.content_margin_top = 5
	sb.content_margin_bottom = 5
	btn.add_theme_stylebox_override("normal", sb)
	btn.add_theme_stylebox_override("hover", sb)
	btn.add_theme_stylebox_override("pressed", sb)
	btn.add_theme_stylebox_override("hover_pressed", sb)
	btn.add_theme_stylebox_override("disabled", sb)

func _add_textarea_to_row(row: HBoxContainer, label_key: String, save_key: String, value: String, field_dict: Dictionary) -> void:
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.npc_editor." + label_key)))
	var te := TextEdit.new()
	te.text = value
	te.custom_minimum_size = Vector2(TEXTAREA_MIN_WIDTH, TEXTAREA_MIN_HEIGHT)
	te.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	te.add_theme_font_size_override("font_size", _style.font_size_small)
	row.add_child(te)
	field_dict[save_key] = te

func _add_dropdown_to_row(row: HBoxContainer, label_key: String, save_key: String, current: String, options: Array, label_to_value: Dictionary, field_dict: Dictionary) -> void:
	row.add_child(_inline_label(Texts.get_text("ui.dev_hub.npc_editor." + label_key)))
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
	field_dict[save_key] = dd

# ============================================================
# Combat stats grid
# ============================================================

func _build_combat_stats_grid(container: VBoxContainer, field_dict: Dictionary) -> void:
	var grid := VBoxContainer.new()
	grid.add_theme_constant_override("separation", 4)
	container.add_child(grid)

	_add_grid_row(grid, [
		_grid_spec("label_attack_speed", "attack_speed", int(_current_data.get("attack_speed", 10)), 1, 999),
		_grid_spec("label_aggro_range", "aggro_range", int(_current_data.get("aggro_range", 8)), 1, 999),
		_grid_spec("label_spawn_timer", "spawn_timer", int(_current_data.get("spawn_timer", 3)), 0, 999999),
	], field_dict)

	_add_grid_row(grid, [
		_grid_spec("label_final_level", "final_level", int(_current_data.get("final_level", 1)), 1, 999),
	], field_dict)

	_add_grid_row(grid, [
		_grid_spec("label_hitpoints", "max_hitpoints", int(_current_data.get("max_hitpoints", 10)), 1, 999999),
	], field_dict)

	_add_grid_row(grid, [
		_grid_spec("label_accuracy", "max_accuracy", int(_current_data.get("max_accuracy", 1)), 1, 999999),
		_grid_spec("label_strength", "max_strength", int(_current_data.get("max_strength", 1)), 1, 999999),
		_grid_spec("label_defence", "max_defence", int(_current_data.get("max_defence", 1)), 1, 999999),
		_grid_spec("label_ether", "max_ether", int(_current_data.get("max_ether", 1)), 1, 999999),
	], field_dict)

	_add_grid_row(grid, [
		_grid_spec("label_melee_bonus", "melee_bonus", int(_current_data.get("melee_bonus", 0)), 0, 999999),
		_grid_spec("label_ranged_bonus", "ranged_bonus", int(_current_data.get("ranged_bonus", 0)), 0, 999999),
		_grid_spec("label_ether_bonus", "ether_bonus", int(_current_data.get("ether_bonus", 0)), 0, 999999),
	], field_dict)

	_add_grid_row(grid, [
		_grid_spec("label_melee_defense", "melee_defense", int(_current_data.get("melee_defense", 0)), 0, 999999),
		_grid_spec("label_ranged_defense", "ranged_defense", int(_current_data.get("ranged_defense", 0)), 0, 999999),
		_grid_spec("label_ether_defense", "ether_defense", int(_current_data.get("ether_defense", 0)), 0, 999999),
	], field_dict)

func _grid_spec(label_key: String, save_key: String, value: int, minv: int, maxv: int) -> Dictionary:
	return {"label": label_key, "key": save_key, "val": value, "min": minv, "max": maxv}

func _add_grid_row(parent: VBoxContainer, cells: Array, field_dict: Dictionary) -> void:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", GRID_CELL_SEP)
	parent.add_child(row)
	for i in range(GRID_COLS):
		if i < cells.size():
			_add_grid_cell(row, cells[i], field_dict)
		else:
			_add_grid_spacer(row)

func _add_grid_cell(row: HBoxContainer, spec: Dictionary, field_dict: Dictionary) -> void:
	var cell := HBoxContainer.new()
	cell.custom_minimum_size = Vector2(GRID_CELL_WIDTH, 0)
	cell.add_theme_constant_override("separation", GRID_LABEL_SPIN_SEP)

	var lbl := Label.new()
	lbl.text = Texts.get_text("ui.dev_hub.npc_editor." + spec["label"])
	lbl.custom_minimum_size = Vector2(GRID_LABEL_WIDTH, 0)
	lbl.clip_text = true
	lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	lbl.add_theme_font_size_override("font_size", _style.font_size_small)
	lbl.add_theme_color_override("font_color", _style.text_muted)
	cell.add_child(lbl)

	var sb := SpinBox.new()
	sb.min_value = spec["min"]
	sb.max_value = spec["max"]
	sb.step = 1
	sb.value = spec["val"]
	sb.custom_minimum_size = Vector2(GRID_SPIN_WIDTH, 0)
	sb.add_theme_font_size_override("font_size", _style.font_size_small)
	cell.add_child(sb)

	row.add_child(cell)
	field_dict[spec["key"]] = sb

func _add_grid_spacer(row: HBoxContainer) -> void:
	var spacer := Control.new()
	spacer.custom_minimum_size = Vector2(GRID_CELL_WIDTH, 0)
	row.add_child(spacer)

# ============================================================
# Shared model helpers
# ============================================================

func _compute_base_fit(model: Node3D, slug: String) -> float:
	if model == null: return 1.0
	var aabb := _measure_world_aabb(model)
	if aabb.size.y < 0.001: return 1.0
	var target_h: float = TARGET_HEIGHT_BY_SLUG.get(slug, 1.8)
	return target_h / aabb.size.y

func _apply_model_scale(model: Node3D, base_fit: float, user_scale: Vector3) -> void:
	if model == null: return
	model.scale = Vector3.ONE * base_fit * user_scale
	model.position = Vector3.ZERO
	var aabb := _measure_world_aabb(model)
	model.position.y -= aabb.position.y

func _measure_world_aabb(root: Node3D) -> AABB:
	var first := true
	var result := AABB()
	for mi in _find_mesh_instances(root):
		if mi.mesh == null: continue
		var world_box: AABB = mi.global_transform * mi.mesh.get_aabb()
		if first: result = world_box; first = false
		else: result = result.merge(world_box)
	return result

func _find_mesh_instances(node: Node) -> Array:
	var out := []
	if node is MeshInstance3D: out.append(node)
	for c in node.get_children():
		out.append_array(_find_mesh_instances(c))
	return out

func _frame_viewer_camera(root: Node3D, viewer_camera: Camera3D, yaw: float, pitch: float, dist_mult: float) -> void:
	var aabb := _measure_world_aabb(root)
	if aabb.size == Vector3.ZERO:
		aabb = AABB(Vector3(-1, 0, -1), Vector3(2, 2, 2))
	var center := aabb.get_center()
	var size := maxf(aabb.size.x, maxf(aabb.size.y, aabb.size.z))
	var dist := maxf(size, 0.5) * 2.5 * dist_mult
	var cp := cos(pitch)
	var sp := sin(pitch)
	var cy := cos(yaw)
	var sy := sin(yaw)
	viewer_camera.global_position = center + Vector3(dist * cp * sy, dist * sp, dist * cp * cy)
	viewer_camera.look_at(center, Vector3.UP)

# ============================================================
# Edit tab: model + camera
# ============================================================

func _load_edit_model(slug: String) -> void:
	if slug == "":
		_clear_edit_model()
		_current_slug = ""
		return
	var is_same := slug == _current_slug
	_clear_edit_model()
	_current_slug = slug
	var scene := NpcRender.get_scene(slug)
	if scene == null: return
	var inst := scene.instantiate() as Node3D
	if inst == null: return
	_model_holder.add_child(inst)
	_current_model = inst
	var base_fit := _compute_base_fit(inst, slug)
	var user_scale := _load_scale_for_slug(slug)
	_apply_model_scale(inst, base_fit, user_scale)
	if not is_same:
		_frame_viewer_camera(inst, _camera, _orbit_yaw, _orbit_pitch, 1.0)
		_orbit_target = _measure_world_aabb(inst).get_center()
		_orbit_distance = (_camera.global_position - _orbit_target).length()
	_update_edit_camera_orbit()

func _clear_edit_model() -> void:
	if _current_model != null and is_instance_valid(_current_model):
		_current_model.queue_free()
	_current_model = null

func _update_edit_camera_orbit() -> void:
	if _camera == null: return
	var cp := cos(_orbit_pitch)
	var sp := sin(_orbit_pitch)
	var cy := cos(_orbit_yaw)
	var sy := sin(_orbit_yaw)
	var offset := Vector3(_orbit_distance * cp * sy, _orbit_distance * sp, _orbit_distance * cp * cy)
	_camera.global_position = _orbit_target + offset
	_camera.look_at(_orbit_target, Vector3.UP)

func _on_edit_viewport_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_MIDDLE:
			_orbiting = mb.pressed
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_orbit_distance = maxf(orbit_min_distance, _orbit_distance * orbit_zoom_step)
			_update_edit_camera_orbit()
			get_viewport().set_input_as_handled()
			return
		if mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_orbit_distance = minf(orbit_max_distance, _orbit_distance / orbit_zoom_step)
			_update_edit_camera_orbit()
			get_viewport().set_input_as_handled()
			return
	elif event is InputEventMouseMotion and _orbiting:
		var mm := event as InputEventMouseMotion
		_orbit_yaw -= mm.relative.x * orbit_mouse_sensitivity
		_orbit_pitch = clampf(_orbit_pitch + mm.relative.y * orbit_mouse_sensitivity, -orbit_pitch_clamp, orbit_pitch_clamp)
		_update_edit_camera_orbit()

# ============================================================
# Cache writes
# ============================================================

func _write_hitbox_to_cache(slug: String, hitbox: Dictionary) -> void:
	if slug.is_empty(): return
	var dir := "%s/%s" % [NpcRender.CACHE_DIR, slug]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open("%s/hitbox.json" % dir, FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(hitbox, "\t"))
	f.close()

func _write_render_to_cache(slug: String, render: Dictionary) -> void:
	if slug.is_empty(): return
	var dir := "%s/%s" % [NpcRender.CACHE_DIR, slug]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	var f := FileAccess.open("%s/render.json" % dir, FileAccess.WRITE)
	if f == null: return
	f.store_string(JSON.stringify(render, "\t"))
	f.close()
