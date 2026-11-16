class_name NpcRenderScaleForm
extends VBoxContainer

signal changed(scale: Vector3)

var _sx: SpinBox
var _sy: SpinBox
var _sz: SpinBox


func _init() -> void:
	add_theme_constant_override("separation", 4)
	_build()


func _build() -> void:
	var p := "ui.dev_hub.npc_editor."

	var r1 := HBoxContainer.new()
	r1.add_theme_constant_override("separation", 6)
	r1.add_child(DevFormFields.compact_label(Texts.get_text(p + "scale_x")))
	_sx = DevFormFields.spin(10, 500, 100)
	_sx.step = 5
	_sx.value_changed.connect(_on_change)
	r1.add_child(_sx)
	r1.add_child(DevFormFields.compact_label(Texts.get_text(p + "scale_y")))
	_sy = DevFormFields.spin(10, 500, 100)
	_sy.step = 5
	_sy.value_changed.connect(_on_change)
	r1.add_child(_sy)
	r1.add_child(DevFormFields.compact_label(Texts.get_text(p + "scale_z")))
	_sz = DevFormFields.spin(10, 500, 100)
	_sz.step = 5
	_sz.value_changed.connect(_on_change)
	r1.add_child(_sz)
	add_child(r1)


func set_scale_value(sc: Vector3) -> void:
	_sx.set_value_no_signal(sc.x * 100)
	_sy.set_value_no_signal(sc.y * 100)
	_sz.set_value_no_signal(sc.z * 100)


func get_scale_value() -> Vector3:
	return Vector3(_sx.value / 100.0, _sy.value / 100.0, _sz.value / 100.0)


func _on_change(_v: float) -> void:
	changed.emit(get_scale_value())
