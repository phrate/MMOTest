class_name DevToggleButton
extends Button
## Reusable toggle. GREEN (default) for boolean options; PURPLE for "active"
## style toggles. Automatically restyles on toggle; no [x] / [ ] prefix —
## the colored background is the state indicator.

enum Kind { GREEN, PURPLE }

var _kind: int = Kind.GREEN
var _on: bool = false


func _init() -> void:
	toggle_mode = true
	focus_mode = Control.FOCUS_NONE
	mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	var s := DevStyle.instance
	add_theme_font_size_override("font_size", s.font_size_body)
	add_theme_color_override("font_color", s.text_primary)
	add_theme_color_override("font_hover_color", s.text_primary)
	add_theme_color_override("font_pressed_color", Color.WHITE)
	add_theme_color_override("font_hover_pressed_color", Color.WHITE)
	add_theme_color_override("font_disabled_color", s.text_muted)
	toggled.connect(_on_toggled)


func setup(label_text: String, pressed: bool, kind: int = Kind.GREEN) -> void:
	_kind = kind
	_on = pressed
	button_pressed = pressed
	self.text = label_text
	_apply_style()


func _on_toggled(pressed: bool) -> void:
	_on = pressed
	_apply_style()


func _apply_style() -> void:
	var sb := StyleBoxFlat.new()
	if _on:
		if _kind == Kind.PURPLE:
			sb.bg_color = Color(0.35, 0.20, 0.45, 1.0)
			sb.border_color = Color(0.70, 0.40, 0.90, 1.0)
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
	add_theme_stylebox_override("normal", sb)
	add_theme_stylebox_override("hover", sb)
	add_theme_stylebox_override("pressed", sb)
	add_theme_stylebox_override("hover_pressed", sb)
	add_theme_stylebox_override("disabled", sb)
