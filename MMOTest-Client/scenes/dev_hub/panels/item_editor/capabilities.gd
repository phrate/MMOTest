class_name ItemEditorCapabilities
extends VBoxContainer
## Inner tabbed sub-editor for equipment and consumable capabilities.
## Subforms are always visible; the "Enabled" toggle only controls whether
## that capability persists to the DB. Callers call set_data(src) to populate
## and get_data() to collect.

const EQUIPMENT_SLOTS := ["head","back","neck","body","belt","legs",
	"wrist_left","wrist_right","hands","ring_1","ring_2","feet",
	"main_hand","off_hand","ammo"]
const EFFECT_TYPES := ["heal","buff","utility"]
const TAB_PADDING := 10
const ROW_SEP := 6

var _has_equipment: bool = false
var _has_consumable: bool = false
var _equipment_widgets: Dictionary = {}
var _consumable_widgets: Dictionary = {}
var _equipment_enable_btn: DevToggleButton
var _consumable_enable_btn: DevToggleButton


func _init() -> void:
	add_theme_constant_override("separation", 4)
	var s := DevStyle.instance

	var tabs := TabContainer.new()
	tabs.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	tabs.size_flags_vertical = Control.SIZE_EXPAND_FILL
	add_child(tabs)

	# Equipment tab
	var eq_margin := _tab_margin()
	tabs.add_child(eq_margin)
	var eq_tab := VBoxContainer.new()
	eq_tab.name = "Equipment"
	eq_tab.add_theme_constant_override("separation", ROW_SEP)
	eq_margin.add_child(eq_tab)
	_equipment_enable_btn = DevToggleButton.new()
	_equipment_enable_btn.setup(Texts.get_text("ui.dev_hub.item_editor.enabled"), false)
	_equipment_enable_btn.toggled.connect(func(p: bool) -> void: _has_equipment = p)
	eq_tab.add_child(_equipment_enable_btn)
	_build_equipment_subform(eq_tab)

	# Consumable tab
	var cs_margin := _tab_margin()
	tabs.add_child(cs_margin)
	var cs_tab := VBoxContainer.new()
	cs_tab.name = "Consumable"
	cs_tab.add_theme_constant_override("separation", ROW_SEP)
	cs_margin.add_child(cs_tab)
	_consumable_enable_btn = DevToggleButton.new()
	_consumable_enable_btn.setup(Texts.get_text("ui.dev_hub.item_editor.enabled"), false)
	_consumable_enable_btn.toggled.connect(func(p: bool) -> void: _has_consumable = p)
	cs_tab.add_child(_consumable_enable_btn)
	_build_consumable_subform(cs_tab)

	tabs.set_tab_title(0, Texts.get_text("ui.dev_hub.item_editor.tab_equipment"))
	tabs.set_tab_title(1, Texts.get_text("ui.dev_hub.item_editor.tab_consumable"))


func _tab_margin() -> MarginContainer:
	var m := MarginContainer.new()
	m.add_theme_constant_override("margin_left", TAB_PADDING)
	m.add_theme_constant_override("margin_right", TAB_PADDING)
	m.add_theme_constant_override("margin_top", TAB_PADDING)
	m.add_theme_constant_override("margin_bottom", TAB_PADDING)
	return m


func _build_equipment_subform(container: VBoxContainer) -> void:
	_equipment_widgets.clear()
	var p := "ui.dev_hub.item_editor."
	DevFormFields.dropdown(container, Texts.get_text(p + "label_equipment_slot"),
		"head", EQUIPMENT_SLOTS, _equipment_widgets, "slot")
	DevFormFields.spin_row(container, Texts.get_text(p + "label_attack_speed_override"),
		0, 0, 999, _equipment_widgets, "attack_speed_override")

	var r1 := DevFormFields.field_row()
	DevFormFields.inline_spin(r1, Texts.get_text(p + "label_attack_melee"), 0, _equipment_widgets, "attack_bonus_melee")
	DevFormFields.inline_spin(r1, Texts.get_text(p + "label_attack_ranged"), 0, _equipment_widgets, "attack_bonus_ranged")
	DevFormFields.inline_spin(r1, Texts.get_text(p + "label_attack_ether"), 0, _equipment_widgets, "attack_bonus_ether")
	container.add_child(r1)

	var r2 := DevFormFields.field_row()
	DevFormFields.inline_spin(r2, Texts.get_text(p + "label_defense_melee"), 0, _equipment_widgets, "defense_bonus_melee")
	DevFormFields.inline_spin(r2, Texts.get_text(p + "label_defense_ranged"), 0, _equipment_widgets, "defense_bonus_ranged")
	DevFormFields.inline_spin(r2, Texts.get_text(p + "label_defense_ether"), 0, _equipment_widgets, "defense_bonus_ether")
	container.add_child(r2)

	var r3 := DevFormFields.field_row()
	DevFormFields.inline_spin(r3, Texts.get_text(p + "label_strength_melee"), 0, _equipment_widgets, "bonus_melee_strength")
	DevFormFields.inline_spin(r3, Texts.get_text(p + "label_strength_ranged"), 0, _equipment_widgets, "bonus_ranged_strength")
	DevFormFields.inline_spin(r3, Texts.get_text(p + "label_strength_ether"), 0, _equipment_widgets, "bonus_ether_strength")
	container.add_child(r3)


func _build_consumable_subform(container: VBoxContainer) -> void:
	_consumable_widgets.clear()
	var p := "ui.dev_hub.item_editor."
	DevFormFields.dropdown(container, Texts.get_text(p + "label_effect_type"),
		"heal", EFFECT_TYPES, _consumable_widgets, "effect_type")
	DevFormFields.spin_row(container, Texts.get_text(p + "label_heal_amount"),
		0, 0, 999, _consumable_widgets, "heal_amount")


func set_data(src: Dictionary) -> void:
	var eq: Variant = src.get("equipment", null)
	_has_equipment = eq != null and typeof(eq) == TYPE_DICTIONARY
	var eq_dict: Dictionary = (eq as Dictionary).duplicate() if _has_equipment else {}

	var cs: Variant = src.get("consumable", null)
	_has_consumable = cs != null and typeof(cs) == TYPE_DICTIONARY
	var cs_dict: Dictionary = (cs as Dictionary).duplicate() if _has_consumable else {}

	_equipment_enable_btn.button_pressed = _has_equipment
	_equipment_enable_btn.emit_signal("toggled", _has_equipment)
	_consumable_enable_btn.button_pressed = _has_consumable
	_consumable_enable_btn.emit_signal("toggled", _has_consumable)

	_apply_widget_values(_equipment_widgets, eq_dict, "head", 0)
	_apply_widget_values(_consumable_widgets, cs_dict, "heal", 0)


func _apply_widget_values(wdict: Dictionary, data: Dictionary, default_dropdown: String, default_spin: int) -> void:
	for key in wdict.keys():
		var w = wdict[key]
		var val: Variant = data.get(key, null)
		if w is SpinBox:
			(w as SpinBox).value = int(val) if val != null else default_spin
		elif w is OptionButton:
			var dd := w as OptionButton
			var current := str(val) if val != null else default_dropdown
			var found := false
			for i in dd.item_count:
				if dd.get_item_text(i) == current:
					dd.select(i)
					found = true
					break
			if not found and dd.item_count > 0:
				dd.select(0)


func get_data() -> Dictionary:
	return {
		"equipment": _collect(_equipment_widgets) if _has_equipment else null,
		"consumable": _collect(_consumable_widgets) if _has_consumable else null,
	}


func _collect(wdict: Dictionary) -> Dictionary:
	var out := {}
	for key in wdict.keys():
		var w = wdict[key]
		if w is SpinBox:
			out[key] = int((w as SpinBox).value)
		elif w is OptionButton:
			var idx: int = (w as OptionButton).selected
			out[key] = (w as OptionButton).get_item_text(idx) if idx >= 0 else ""
	return out