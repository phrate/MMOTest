class_name OverheadHealthBar
extends Node3D
## Billboarded HP bar. Opaque quads with render_priority draw ordering.

const BG_WIDTH  := 0.9
const BG_HEIGHT := 0.12
const FG_WIDTH  := 0.86
const FG_HEIGHT := 0.08

var _fraction: float = 1.0
var _fg: MeshInstance3D


func _ready() -> void:
	_build()


func _build() -> void:
	var bg_quad := QuadMesh.new()
	bg_quad.size = Vector2(BG_WIDTH, BG_HEIGHT)
	var bg := MeshInstance3D.new()
	bg.mesh = bg_quad
	bg.material_override = _mat(HealthBarColors.OVERHEAD_BG, 0)
	bg.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(bg)

	var fg_quad := QuadMesh.new()
	fg_quad.size = Vector2(FG_WIDTH, FG_HEIGHT)
	_fg = MeshInstance3D.new()
	_fg.mesh = fg_quad
	_fg.material_override = _mat(HealthBarColors.OVERHEAD_FILL, 1)
	_fg.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_fg.position.z = 0.001
	add_child(_fg)
	_apply()


func _mat(c: Color, priority: int) -> StandardMaterial3D:
	var m := StandardMaterial3D.new()
	m.albedo_color = Color(c.r, c.g, c.b, 1.0)   # force opaque
	m.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	m.billboard_mode = BaseMaterial3D.BILLBOARD_ENABLED
	m.billboard_keep_scale = true
	m.no_depth_test = true
	m.transparency = BaseMaterial3D.TRANSPARENCY_DISABLED
	m.cull_mode = BaseMaterial3D.CULL_DISABLED
	m.render_priority = priority
	return m


func set_fraction(f: float) -> void:
	_fraction = clampf(f, 0.0, 1.0)
	if _fg: _apply()


func _apply() -> void:
	_fg.scale.x = maxf(_fraction, 0.001)
	_fg.visible = _fraction > 0.001
