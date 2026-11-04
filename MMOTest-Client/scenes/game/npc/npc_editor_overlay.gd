extends Control
## Modal admin panel for editing an NPC live. Center-right of viewport.

var _npc_id: int = 0
var _data: Dictionary = {}
var _fields: Dictionary = {}

var _panel: PanelContainer
var _title_label: Label
var _fields_container: VBoxContainer
var _save_button: Button


func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	mouse_filter = Control.MOUSE_FILTER_STOP
	visible = false
	add_to_group("modal_blockers")
	_build_ui()
	Network.npc_editor_data.connect(_on_data)
	Network.npc_editor_saved.connect(_on_saved)


func _build_ui() -> void:
	var dim := ColorRect.new()
	dim.color = Color(0, 0, 0, 0.55)
	dim.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	dim.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(dim)

	_panel = PanelContainer.new()
	_panel.anchor_left = 0.5
	_panel.anchor_right = 0.5
	_panel.anchor_top = 0.5
	_panel.anchor_bottom = 0.5
	_panel.offset_left = 60.0
	_panel.offset_right = 560.0
	_panel.offset_top = -300.0
	_panel.offset_bottom = 300.0
	_panel.custom_minimum_size = Vector2(500, 600)
	add_child(_panel)

	var vbox := VBoxContainer.new()
	vbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_panel.add_child(vbox)

	_title_label = Label.new()
	_title_label.add_theme_font_size_override("font_size", 18)
	vbox.add_child(_title_label)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vbox.add_child(scroll)

	_fields_container = VBoxContainer.new()
	_fields_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(_fields_container)

	var row := HBoxContainer.new()
	vbox.add_child(row)

	_save_button = Button.new()
	_save_button.text = "Save"
	_save_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_save_button.pressed.connect(_on_save_pressed)
	row.add_child(_save_button)

	var close_button := Button.new()
	close_button.text = "Close"
	close_button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	close_button.pressed.connect(close_panel)
	row.add_child(close_button)


func open_for(npc_id: int) -> void:
	if get_viewport().gui_get_focus_owner() != null:
		get_viewport().gui_get_focus_owner().release_focus()
	_npc_id = npc_id
	_data = {}
	_fields.clear()
	for c in _fields_container.get_children(): c.queue_free()
	_title_label.text = "Loading NPC #%d..." % npc_id
	visible = true
	Network.send_npc_editor_get(npc_id)


func close_panel() -> void:
	visible = false
	_npc_id = 0
	_data = {}


func _on_data(npc: Dictionary) -> void:
	if int(npc.get("id", 0)) != _npc_id: return
	_data = npc
	_title_label.text = "%s (#%d)" % [str(npc.get("name", "?")), _npc_id]
	_build_fields()


func _build_fields() -> void:
	for c in _fields_container.get_children(): c.queue_free()
	_fields.clear()

	_add_string("display", "Display", str(_data.get("display", "")))
	_add_bool("attackable", "Attackable", bool(_data.get("attackable", true)))
	_add_bool("aggressive", "Aggressive", bool(_data.get("aggressive", false)))
	_add_int("aggro_range", "Aggro Range", int(_data.get("aggro_range", 8)), 1)
	_add_string("ai_package_state", "AI Package", str(_data.get("ai_package_state", "idle")))
	_add_string("ai_action_state", "AI Action", str(_data.get("ai_action_state", "none")))
	_add_string("loot_table_name", "Loot Table", str(_data.get("loot_table_name", "common_loot_1")))
	_add_int("spawn_timer", "Spawn Timer (s)", int(_data.get("spawn_timer", 3)), 1)

	_add_sep()
	_add_int("final_level", "Final Level", int(_data.get("final_level", 1)), 1)
	_add_int("hitpoints", "Hitpoints", int(_data.get("hitpoints", 3)), 0)
	_add_int("max_hitpoints", "Max Hitpoints", int(_data.get("max_hitpoints", 3)), 1)
	_add_int("attack_speed", "Attack Speed (ticks)", int(_data.get("attack_speed", 10)), 1)
	_add_int("accuracy", "Accuracy", int(_data.get("accuracy", 1)), 1)
	_add_int("strength", "Strength", int(_data.get("strength", 1)), 1)
	_add_int("defence", "Defence", int(_data.get("defence", 1)), 1)
	_add_int("ether", "Ether", int(_data.get("ether", 1)), 1)
	_add_int("max_ether", "Max Ether", int(_data.get("max_ether", 1)), 1)

	_add_sep()
	_add_int("melee_bonus", "Melee Bonus", int(_data.get("melee_bonus", 0)), 0)
	_add_int("ranged_bonus", "Ranged Bonus", int(_data.get("ranged_bonus", 0)), 0)
	_add_int("ether_bonus", "Ether Bonus", int(_data.get("ether_bonus", 0)), 0)
	_add_int("melee_defense", "Melee Defense", int(_data.get("melee_defense", 0)), 0)
	_add_int("ranged_defense", "Ranged Defense", int(_data.get("ranged_defense", 0)), 0)
	_add_int("ether_defense", "Ether Defense", int(_data.get("ether_defense", 0)), 0)


func _add_row(label_text: String, c: Control) -> void:
	var row := HBoxContainer.new()
	_fields_container.add_child(row)
	var lbl := Label.new()
	lbl.text = label_text
	lbl.custom_minimum_size = Vector2(140, 0)
	row.add_child(lbl)
	c.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(c)


func _add_sep() -> void:
	_fields_container.add_child(HSeparator.new())


func _add_int(key: String, label: String, v: int, minv: int) -> void:
	var sb := SpinBox.new()
	sb.min_value = minv
	sb.max_value = 999999
	sb.step = 1
	sb.value = v
	_fields[key] = sb
	_add_row(label, sb)


func _add_string(key: String, label: String, v: String) -> void:
	var le := LineEdit.new()
	le.text = v
	_fields[key] = le
	_add_row(label, le)


func _add_bool(key: String, label: String, v: bool) -> void:
	var cb := CheckBox.new()
	cb.button_pressed = v
	_fields[key] = cb
	_add_row(label, cb)


func _on_save_pressed() -> void:
	if _npc_id == 0: return
	var updates := {}
	for key in _fields.keys():
		var c = _fields[key]
		if c is SpinBox: updates[key] = int((c as SpinBox).value)
		elif c is LineEdit: updates[key] = (c as LineEdit).text
		elif c is CheckBox: updates[key] = (c as CheckBox).button_pressed
	Network.send_npc_editor_save(_npc_id, updates)
	_save_button.text = "Saving..."
	_save_button.disabled = true


func _on_saved(ok: bool, npc_id: int) -> void:
	if npc_id != _npc_id: return
	_save_button.disabled = false
	_save_button.text = "Save"
	if ok:
		Network.send_npc_editor_get(_npc_id)


func _unhandled_input(event: InputEvent) -> void:
	if not visible: return
	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		close_panel()
		get_viewport().set_input_as_handled()
