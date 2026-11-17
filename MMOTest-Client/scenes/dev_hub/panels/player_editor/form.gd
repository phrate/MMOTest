class_name PlayerEditorForm
extends VBoxContainer
## The Edit sub-tab. Assembles the 3×2 grid:
##   model | identity
##   inventory | actions
##   bank | levels
##
## Level edits are batched — SpinBox changes only mark entries dirty; the
## Save button at the bottom of the Levels section commits them all at once.

signal admin_changed(player_id: int, is_admin: bool)
signal level_changed(player_id: int, skill_id: String, new_level: int)
signal teleport_requested(player_id: int, x: int, y: int)
signal teleport_to_admin_requested(player_id: int)
signal teleport_admin_to_requested(player_id: int)

const MAX_LEVEL := 100
const COMBAT_SKILLS := ["hitpoints", "accuracy", "strength", "defence", "ether"]
const SKILLS_SKILLS := ["woodcutting", "mining", "fishing", "medicine", "engineering"]
const SECTION_RATIO_LEFT := 5.0
const SECTION_RATIO_RIGHT := 7.0

var bank_min_height: int = 600  # set by parent panel before set_data()

var _src: Dictionary = {}

var _model_viewer: DevModelViewer
var _admin_toggle: DevToggleButton
var _inventory_grid: PlayerEditorSlotsGrid
var _bank_grid: PlayerEditorSlotsGrid
var _levels_save_btn: Button
var _level_rows: Dictionary = {}   # skill_id -> {"spin": SpinBox, "xp": Label}
var _dirty_levels: Dictionary = {} # skill_id -> new_level (pending commit)
var _guard_levels: bool = false

var _hover_layer: CanvasLayer
var _hover_panel: PanelContainer
var _hover_label: Label


func _init() -> void:
	add_theme_constant_override("separation", 8)
	_build_hover()


func set_data(data: Dictionary) -> void:
	_src = data.duplicate(true)
	_rebuild()


func clear() -> void:
	_src = {}
	for c in get_children():
		if c == _hover_layer: continue
		remove_child(c)
		c.queue_free()
	_level_rows.clear()
	_dirty_levels.clear()
	_model_viewer = null
	_admin_toggle = null
	_inventory_grid = null
	_bank_grid = null
	_levels_save_btn = null


func update_level_display(skill_id: String, new_level: int, current_xp: int) -> void:
	var entry = _level_rows.get(skill_id)
	if entry == null: return
	_guard_levels = true
	(entry["spin"] as SpinBox).set_value_no_signal(new_level)
	_guard_levels = false
	(entry["xp"] as Label).text = Texts.get_text(
		"ui.dev_hub.player_editor.levels_xp_label",
		{"xp": NumberText.with_commas(current_xp)})
	# Sync _src so subsequent dirty tracking compares against server truth.
	var skills: Array = _src.get("skills", [])
	for sk in skills:
		if typeof(sk) == TYPE_DICTIONARY and str(sk.get("id", "")) == skill_id:
			sk["level"] = new_level
			sk["max"] = new_level
			break
	# Safety net: refresh button state after every server reply.
	_update_levels_save_state()


func _process(_delta: float) -> void:
	if _hover_panel != null and _hover_panel.visible:
		_update_hover_position()


# ============================================================
# Layout
# ============================================================

func _rebuild() -> void:
	for c in get_children():
		if c == _hover_layer: continue
		remove_child(c)
		c.queue_free()
	_level_rows.clear()
	_dirty_levels.clear()

	var row0 := HBoxContainer.new()
	row0.add_theme_constant_override("separation", 8)
	add_child(row0)
	_build_model_section(row0)
	_build_identity_section(row0)

	var row1 := HBoxContainer.new()
	row1.add_theme_constant_override("separation", 8)
	add_child(row1)
	_build_inventory_section(row1)
	_build_actions_section(row1)

	var row2 := HBoxContainer.new()
	row2.add_theme_constant_override("separation", 8)
	add_child(row2)
	_build_bank_section(row2)
	_build_levels_section(row2)


# ---------- MODEL ----------

func _build_model_section(parent: HBoxContainer) -> void:
	var sec := DevSection.new()
	sec.set_ratio(SECTION_RATIO_LEFT)
	sec.add_header(Texts.get_text("ui.dev_hub.player_editor.section_model"))

	_model_viewer = DevModelViewer.new()
	_model_viewer.custom_minimum_size = Vector2(280, 300)
	_model_viewer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	sec.content.add_child(_model_viewer)
	parent.add_child(sec)

	call_deferred("_load_default_model")


func _load_default_model() -> void:
	if _model_viewer == null or not is_instance_valid(_model_viewer): return
	if not _model_viewer.is_node_ready(): return
	_model_viewer.load_model("player")
	_model_viewer.frame_camera()
	# 50% closer than the frame_camera default (2.5 → ~1.667).
	# Adjust this multiplier if the preview wants more/less initial zoom.
	_model_viewer.orbit_distance = _model_viewer.orbit_distance * (1.25 / 3.0)
	_model_viewer.apply_orbit()


# ---------- IDENTITY ----------

func _build_identity_section(parent: HBoxContainer) -> void:
	var sec := DevSection.new()
	sec.set_ratio(SECTION_RATIO_RIGHT)
	sec.add_header(Texts.get_text("ui.dev_hub.player_editor.section_identity"))

	var table := VBoxContainer.new()
	table.add_theme_constant_override("separation", 2)
	sec.content.add_child(table)

	var p := "ui.dev_hub.player_editor.identity_"
	var gold := Color(1.0, 0.85, 0.30)
	var green := Color(0.4, 0.9, 0.4)
	var red := Color(0.9, 0.4, 0.4)
	var white := Color(1, 1, 1)
	var online := bool(_src.get("is_online", false))

	var rows: Array = [
		{"label": Texts.get_text(p + "id"),
		 "value": str(int(_src.get("id", 0))), "color": white},
		{"label": Texts.get_text(p + "username"),
		 "value": str(_src.get("username", "")), "color": white},
		{"label": Texts.get_text(p + "tile"),
		 "value": "[x%d, y%d, z%d]" % [
			int(_src.get("tile_x", 0)),
			int(_src.get("tile_y", 0)),
			int(_src.get("tile_z", 0))], "color": white},
		{"label": Texts.get_text(p + "final_level"),
		 "value": str(int(_src.get("final_level", 1))), "color": white},
		{"label": Texts.get_text(p + "total_level"),
		 "value": str(int(_src.get("total_level", 0))), "color": white},
		{"label": Texts.get_text(p + "credits"),
		 "value": NumberText.with_commas(int(_src.get("credits", 0))), "color": gold},
		{"label": Texts.get_text(p + "bank_capacity"),
		 "value": str(int(_src.get("bank_capacity", 100))), "color": white},
		{"label": Texts.get_text(p + "status"),
		 "value": Texts.get_text(p + ("online" if online else "offline")),
		 "color": green if online else red},
	]

	for i in rows.size():
		table.add_child(_make_identity_row(rows[i], i % 2 == 0))

	parent.add_child(sec)


func _make_identity_row(data: Dictionary, alt: bool) -> Control:
	var s := DevStyle.instance
	var panel := PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.18, 0.18, 0.22, 1.0) if alt else Color(0.14, 0.14, 0.17, 1.0)
	style.content_margin_left = 10
	style.content_margin_right = 10
	style.content_margin_top = 7
	style.content_margin_bottom = 7
	style.corner_radius_top_left = 3
	style.corner_radius_top_right = 3
	style.corner_radius_bottom_left = 3
	style.corner_radius_bottom_right = 3
	panel.add_theme_stylebox_override("panel", style)

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 12)
	panel.add_child(row)

	var lbl := Label.new()
	lbl.text = str(data["label"])
	lbl.custom_minimum_size = Vector2(140, 0)
	lbl.add_theme_font_size_override("font_size", 15)
	lbl.add_theme_color_override("font_color", s.text_muted)
	row.add_child(lbl)

	var v := Label.new()
	v.text = str(data["value"])
	v.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	v.add_theme_font_size_override("font_size", 16)
	v.add_theme_color_override("font_color", data["color"])
	row.add_child(v)

	return panel


# ---------- INVENTORY ----------

func _build_inventory_section(parent: HBoxContainer) -> void:
	var sec := DevSection.new()
	sec.set_ratio(SECTION_RATIO_LEFT)
	sec.add_header(Texts.get_text("ui.dev_hub.player_editor.section_inventory"))

	_inventory_grid = PlayerEditorSlotsGrid.new()
	_inventory_grid.configure(4, 44)
	_inventory_grid.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	_inventory_grid.set_total_slots(28)
	_inventory_grid.set_items(_src.get("inventory", []))
	_inventory_grid.hover_show.connect(_show_hover)
	_inventory_grid.hover_hide.connect(_hide_hover)
	sec.content.add_child(_inventory_grid)

	parent.add_child(sec)


# ---------- BANK ----------

func _build_bank_section(parent: HBoxContainer) -> void:
	var sec := DevSection.new()
	sec.set_ratio(SECTION_RATIO_LEFT)
	sec.add_header(Texts.get_text("ui.dev_hub.player_editor.section_bank"))

	var capacity := int(_src.get("bank_capacity", 100))

	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.custom_minimum_size = Vector2(0, bank_min_height)
	scroll.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	sec.content.add_child(scroll)

	_bank_grid = PlayerEditorSlotsGrid.new()
	_bank_grid.configure(8, 40)
	_bank_grid.set_total_slots(capacity)
	_bank_grid.set_items(_src.get("bank", []))
	_bank_grid.hover_show.connect(_show_hover)
	_bank_grid.hover_hide.connect(_hide_hover)
	scroll.add_child(_bank_grid)

	parent.add_child(sec)


# ---------- ACTIONS ----------

func _build_actions_section(parent: HBoxContainer) -> void:
	var sec := DevSection.new()
	sec.set_ratio(SECTION_RATIO_RIGHT)
	sec.add_header(Texts.get_text("ui.dev_hub.player_editor.section_actions"))

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 10)
	sec.content.add_child(vbox)

	var p := "ui.dev_hub.player_editor."

	var admin_row := HBoxContainer.new()
	admin_row.add_theme_constant_override("separation", 8)
	vbox.add_child(admin_row)
	_admin_toggle = DevToggleButton.new()
	_admin_toggle.setup(Texts.get_text(p + "action_is_admin"),
		bool(_src.get("is_admin", false)), DevToggleButton.Kind.PURPLE)
	_admin_toggle.toggled.connect(_on_admin_toggled)
	admin_row.add_child(_admin_toggle)

	var red_row := HBoxContainer.new()
	red_row.add_theme_constant_override("separation", 8)
	vbox.add_child(red_row)
	var red := Color(0.62, 0.20, 0.20)
	_add_stub_button(red_row, Texts.get_text(p + "action_force_logout"), red)
	_add_stub_button(red_row, Texts.get_text(p + "action_mute"), red)
	_add_stub_button(red_row, Texts.get_text(p + "action_ban"), red)

	var tp_row := HBoxContainer.new()
	tp_row.add_theme_constant_override("separation", 8)
	vbox.add_child(tp_row)
	var blue := Color(0.28, 0.35, 0.62)
	var tp_tiles := DevFormFields.action_button(Texts.get_text(p + "action_teleport_to_tiles"), blue, 36, 14)
	tp_tiles.pressed.connect(_on_teleport_to_tiles_pressed)
	tp_row.add_child(tp_tiles)
	var tp_me := DevFormFields.action_button(Texts.get_text(p + "action_teleport_to_me"), blue, 36, 14)
	tp_me.pressed.connect(_on_teleport_to_me_pressed)
	tp_row.add_child(tp_me)
	var tp_admin := DevFormFields.action_button(Texts.get_text(p + "action_teleport_me_to"), blue, 36, 14)
	tp_admin.pressed.connect(_on_teleport_admin_to_pressed)
	tp_row.add_child(tp_admin)

	parent.add_child(sec)


func _add_stub_button(parent: HBoxContainer, text: String, color: Color) -> void:
	var b := DevFormFields.action_button(text, color, 36, 14)
	b.pressed.connect(func() -> void: pass)
	parent.add_child(b)


# ---------- LEVELS ----------

func _build_levels_section(parent: HBoxContainer) -> void:
	var sec := DevSection.new()
	sec.set_ratio(SECTION_RATIO_RIGHT)
	sec.add_header(Texts.get_text("ui.dev_hub.player_editor.section_levels"))

	var by_id := {}
	for sk in _src.get("skills", []):
		if typeof(sk) != TYPE_DICTIONARY: continue
		by_id[str(sk.get("id", ""))] = sk

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 16)
	sec.content.add_child(row)

	var combat_col := VBoxContainer.new()
	combat_col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	combat_col.add_theme_constant_override("separation", 4)
	row.add_child(combat_col)
	combat_col.add_child(_make_group_header(
		Texts.get_text("ui.dev_hub.player_editor.levels_combat_header")))
	for skill_id in COMBAT_SKILLS:
		_build_level_row(combat_col, skill_id, by_id.get(skill_id, {}))

	var skills_col := VBoxContainer.new()
	skills_col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	skills_col.add_theme_constant_override("separation", 4)
	row.add_child(skills_col)
	skills_col.add_child(_make_group_header(
		Texts.get_text("ui.dev_hub.player_editor.levels_skills_header")))
	for skill_id in SKILLS_SKILLS:
		_build_level_row(skills_col, skill_id, by_id.get(skill_id, {}))

	_levels_save_btn = DevFormFields.action_button(
		Texts.get_text("ui.dev_hub.player_editor.levels_save_button"),
		Color(0.28, 0.62, 0.28), 40, 16)
	_levels_save_btn.disabled = true
	_levels_save_btn.pressed.connect(_on_levels_save_pressed)
	sec.content.add_child(_levels_save_btn)

	parent.add_child(sec)


func _build_level_row(parent: VBoxContainer, skill_id: String, data: Dictionary) -> void:
	var s := DevStyle.instance
	var r := HBoxContainer.new()
	r.add_theme_constant_override("separation", 6)
	parent.add_child(r)

	var name_lbl := Label.new()
	name_lbl.text = Texts.get_text("game.skills.name." + skill_id)
	name_lbl.custom_minimum_size = Vector2(90, 0)
	name_lbl.add_theme_font_size_override("font_size", s.font_size_small)
	name_lbl.add_theme_color_override("font_color", s.text_muted)
	r.add_child(name_lbl)

	var spin := SpinBox.new()
	spin.min_value = 1
	spin.max_value = MAX_LEVEL
	spin.step = 1
	spin.value = int(data.get("level", 1))
	spin.custom_minimum_size = Vector2(70, 0)
	spin.update_on_text_changed = false
	spin.add_theme_font_size_override("font_size", s.font_size_small)
	spin.value_changed.connect(_on_level_spin_changed.bind(skill_id))
	r.add_child(spin)

	var xp_lbl := Label.new()
	xp_lbl.text = Texts.get_text("ui.dev_hub.player_editor.levels_xp_label", {
		"xp": NumberText.with_commas(int(data.get("current_xp", 0)))})
	xp_lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	xp_lbl.add_theme_font_size_override("font_size", s.font_size_small)
	xp_lbl.add_theme_color_override("font_color", s.text_muted)
	r.add_child(xp_lbl)

	_level_rows[skill_id] = {"spin": spin, "xp": xp_lbl}


func _make_group_header(text: String) -> Control:
	var s := DevStyle.instance
	var c := CenterContainer.new()
	var lbl := Label.new()
	lbl.text = text
	lbl.add_theme_font_size_override("font_size", s.font_size_tab)
	lbl.add_theme_color_override("font_color", s.text_heading)
	c.add_child(lbl)
	return c


# ============================================================
# Handlers
# ============================================================

func _on_admin_toggled(pressed: bool) -> void:
	var pid := int(_src.get("id", 0))
	if pid <= 0: return
	admin_changed.emit(pid, pressed)


func _on_level_spin_changed(v: float, skill_id: String) -> void:
	if _guard_levels: return
	var pid := int(_src.get("id", 0))
	if pid <= 0: return
	var current_level := 1
	for sk in _src.get("skills", []):
		if typeof(sk) == TYPE_DICTIONARY and str(sk.get("id", "")) == skill_id:
			current_level = int(sk.get("level", 1))
			break
	var new_level := int(v)
	if new_level == current_level:
		_dirty_levels.erase(skill_id)
	else:
		_dirty_levels[skill_id] = new_level
	_update_levels_save_state()


func _update_levels_save_state() -> void:
	if _levels_save_btn == null or not is_instance_valid(_levels_save_btn): return
	if _dirty_levels.is_empty():
		_levels_save_btn.disabled = true
	else:
		_levels_save_btn.disabled = false


func _on_levels_save_pressed() -> void:
	if _dirty_levels.is_empty(): return
	var pid := int(_src.get("id", 0))
	if pid <= 0: return
	var snapshot := _dirty_levels.duplicate()
	_dirty_levels.clear()
	_levels_save_btn.disabled = true
	_levels_save_btn.text = Texts.get_text("ui.dev_hub.player_editor.levels_saving")
	for skill_id in snapshot.keys():
		level_changed.emit(pid, skill_id, int(snapshot[skill_id]))
	await get_tree().create_timer(1.2).timeout
	if _levels_save_btn != null and is_instance_valid(_levels_save_btn):
		_levels_save_btn.text = Texts.get_text("ui.dev_hub.player_editor.levels_save_button")
		# Re-evaluate disabled state — if the user changed a spinner while
		# the save was in flight, the button should already be enabled; if
		# nothing changed, it correctly stays disabled.
		_update_levels_save_state()


func _on_teleport_to_tiles_pressed() -> void:
	var p := "ui.dev_hub.player_editor."
	var dlg := ConfirmationDialog.new()
	dlg.title = Texts.get_text(p + "teleport_dialog_title")
	dlg.ok_button_text = Texts.get_text(p + "teleport_dialog_ok")

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 6)
	dlg.add_child(vbox)

	var row_x := HBoxContainer.new()
	row_x.add_theme_constant_override("separation", 6)
	vbox.add_child(row_x)
	row_x.add_child(DevFormFields.compact_label(Texts.get_text(p + "teleport_dialog_x")))
	var x_edit := LineEdit.new()
	x_edit.text = str(int(_src.get("tile_x", 0)))
	x_edit.custom_minimum_size = Vector2(100, 0)
	row_x.add_child(x_edit)

	var row_y := HBoxContainer.new()
	row_y.add_theme_constant_override("separation", 6)
	vbox.add_child(row_y)
	row_y.add_child(DevFormFields.compact_label(Texts.get_text(p + "teleport_dialog_y")))
	var y_edit := LineEdit.new()
	y_edit.text = str(int(_src.get("tile_y", 0)))
	y_edit.custom_minimum_size = Vector2(100, 0)
	row_y.add_child(y_edit)

	dlg.confirmed.connect(func() -> void:
		var pid := int(_src.get("id", 0))
		if pid > 0 and x_edit.text.is_valid_int() and y_edit.text.is_valid_int():
			teleport_requested.emit(pid, int(x_edit.text), int(y_edit.text))
		dlg.queue_free())
	dlg.canceled.connect(func() -> void: dlg.queue_free())
	add_child(dlg)
	dlg.popup_centered()
	x_edit.grab_focus()


func _on_teleport_to_me_pressed() -> void:
	var pid := int(_src.get("id", 0))
	if pid > 0:
		teleport_to_admin_requested.emit(pid)


func _on_teleport_admin_to_pressed() -> void:
	var pid := int(_src.get("id", 0))
	if pid > 0:
		teleport_admin_to_requested.emit(pid)


# ============================================================
# Hover infobox
# ============================================================

func _build_hover() -> void:
	_hover_layer = CanvasLayer.new()
	_hover_layer.layer = 52
	add_child(_hover_layer)

	_hover_panel = PanelContainer.new()
	_hover_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_panel.visible = false
	_hover_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	_hover_layer.add_child(_hover_panel)

	_hover_label = Label.new()
	_hover_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_label.add_theme_font_size_override("font_size", UIStyle.FONT_SIZE)
	_hover_label.add_theme_constant_override("outline_size", UIStyle.OUTLINE_SIZE)
	_hover_label.add_theme_color_override("font_outline_color", UIStyle.OUTLINE_COLOR)
	_hover_panel.add_child(_hover_label)


func _show_hover(text: String) -> void:
	if not is_instance_valid(_hover_label) or not is_instance_valid(_hover_panel): return
	_hover_label.text = text
	_hover_panel.visible = true
	_update_hover_position()


func _hide_hover() -> void:
	if not is_instance_valid(_hover_panel): return
	_hover_panel.visible = false


func _update_hover_position() -> void:
	if not is_instance_valid(_hover_panel): return
	_hover_panel.reset_size()
	var mouse := get_viewport().get_mouse_position()
	var vp := get_viewport().get_visible_rect().size
	var pos := mouse + Vector2(16, -_hover_panel.size.y - 8)
	pos.x = min(pos.x, vp.x - _hover_panel.size.x - 4)
	pos.y = max(pos.y, 4.0)
	_hover_panel.position = Vector2i(pos)
