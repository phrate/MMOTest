class_name DevSection
extends PanelContainer
## A section panel: darker rounded backdrop with 10px inner padding, optional
## centered header, and a `content` VBoxContainer where callers add fields.
## Used for both form layout and as a generic grouping container.

const BG_COLOR := Color(0.16, 0.16, 0.20, 1.0)
const PADDING := 10
const HEADER_HEIGHT := 28

var content: VBoxContainer


func _init() -> void:
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var sb := StyleBoxFlat.new()
	sb.bg_color = BG_COLOR
	sb.corner_radius_top_left = 4
	sb.corner_radius_top_right = 4
	sb.corner_radius_bottom_left = 4
	sb.corner_radius_bottom_right = 4
	sb.content_margin_left = PADDING
	sb.content_margin_right = PADDING
	sb.content_margin_top = PADDING
	sb.content_margin_bottom = PADDING
	add_theme_stylebox_override("panel", sb)
	content = VBoxContainer.new()
	content.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	content.add_theme_constant_override("separation", 4)
	add_child(content)


func set_ratio(r: float) -> void:
	size_flags_stretch_ratio = r


func add_header(text: String) -> void:
	var s := DevStyle.instance
	var center := CenterContainer.new()
	center.custom_minimum_size = Vector2(0, HEADER_HEIGHT)
	var lbl := Label.new()
	lbl.text = text
	lbl.add_theme_font_size_override("font_size", s.font_size_tab)
	lbl.add_theme_color_override("font_color", s.text_heading)
	center.add_child(lbl)
	content.add_child(center)