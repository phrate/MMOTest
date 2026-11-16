class_name NpcRenderHitboxForm
extends VBoxContainer

signal changed(config: Dictionary)

const ROW_SEP := 4

var _type_dd: OptionButton
var _radius: SpinBox
var _height: SpinBox
var _ox: SpinBox
var _oy: SpinBox
var _oz: SpinBox
var _config: Dictionary = {}


func _init() -> void:
	add_theme_constant_override("separation", ROW_SEP)


func set_config(cfg: Dictionary) -> void:
	_config = cfg.duplicate()
	_rebuild()


func get_config() -> Dictionary:
	return _config.duplicate()


func _rebuild() -> void:
	for c in get_children():
		remove_child(c)
		c.queue_free()

	var is_capsule := str(_config.get("type", "capsule")) == "capsule"
	var p := "ui.dev_hub.npc_editor."

	var r_type := DevFormFields.field_row()
	r_type.add_child(DevFormFields.compact_label(Texts.get_text(p + "hitbox_type")))
	_type_dd = OptionButton.new()
	_type_dd.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_type_dd.add_item(Texts.get_text(p + "hitbox_type_sphere"), 0)
	_type_dd.add_item(Texts.get_text(p + "hitbox_type_capsule"), 1)
	_type_dd.select(1 if is_capsule else 0)
	_type_dd.item_selected.connect(_on_type_selected)
	r_type.add_child(_type_dd)
	add_child(r_type)

	var r_rad := DevFormFields.field_row()
	r_rad.add_child(DevFormFields.compact_label(Texts.get_text(p + "hitbox_radius")))
	_radius = DevFormFields.spin(0.05 * 100, 10 * 100, int(float(_config.get("radius", 0.4)) * 100))
	_radius.step = 5
	_radius.value_changed.connect(_on_radius_changed)
	r_rad.add_child(_radius)
	add_child(r_rad)

	if is_capsule:
		var r_h := DevFormFields.field_row()
		r_h.add_child(DevFormFields.compact_label(Texts.get_text(p + "hitbox_height")))
		_height = DevFormFields.spin(10, 1000, int(float(_config.get("height", 1.8)) * 100))
		_height.step = 5
		_height.value_changed.connect(_on_height_changed)
		r_h.add_child(_height)
		add_child(r_h)
	else:
		_height = null

	var r_off := DevFormFields.field_row()
	r_off.add_child(DevFormFields.compact_label(Texts.get_text(p + "hitbox_offset_x")))
	_ox = DevFormFields.spin(-1000, 1000, int(float(_config.get("offset_x", 0.0)) * 100))
	_ox.step = 5
	_ox.value_changed.connect(_on_offset_changed)
	r_off.add_child(_ox)

	r_off.add_child(DevFormFields.compact_label(Texts.get_text(p + "hitbox_offset_y")))
	_oy = DevFormFields.spin(-1000, 1000, int(float(_config.get("offset_y", 0.9)) * 100))
	_oy.step = 5
	_oy.value_changed.connect(_on_offset_changed)
	r_off.add_child(_oy)

	r_off.add_child(DevFormFields.compact_label(Texts.get_text(p + "hitbox_offset_z")))
	_oz = DevFormFields.spin(-1000, 1000, int(float(_config.get("offset_z", 0.0)) * 100))
	_oz.step = 5
	_oz.value_changed.connect(_on_offset_changed)
	r_off.add_child(_oz)
	add_child(r_off)


func _on_type_selected(idx: int) -> void:
	_config["type"] = "capsule" if idx == 1 else "sphere"
	_rebuild()
	changed.emit(get_config())


func _on_radius_changed(_v: float) -> void:
	_config["radius"] = _radius.value / 100.0
	if str(_config.get("type", "capsule")) == "capsule" and _height != null and float(_config.get("height", 1.8)) < _config["radius"] * 2.0 + 0.05:
		_config["height"] = _config["radius"] * 2.0 + 0.05
		_height.set_value_no_signal(_config["height"] * 100)
	changed.emit(get_config())


func _on_height_changed(_v: float) -> void:
	_config["height"] = _height.value / 100.0
	changed.emit(get_config())


func _on_offset_changed(_v: float) -> void:
	_config["offset_x"] = _ox.value / 100.0
	_config["offset_y"] = _oy.value / 100.0
	_config["offset_z"] = _oz.value / 100.0
	changed.emit(get_config())