class_name DevStyle
extends Resource
## Shared styling for the DevHub and every DevPanel.
##
## Every visual and layout value is an @export var, editable in the inspector
## by double-clicking dev_style.tres in the FileSystem dock. Factory methods
## below build StyleBoxFlat objects from these values.
##
## Runtime access: DevStyle.instance (lazily loads from DEFAULT_PATH).
## If the .tres is missing, falls back to script defaults — no crash.

const DEFAULT_PATH := "res://scenes/dev_hub/dev_style.tres"

static var _instance: DevStyle = null
static var instance: DevStyle:
	get:
		if _instance == null:
			if ResourceLoader.exists(DEFAULT_PATH):
				_instance = load(DEFAULT_PATH) as DevStyle
			if _instance == null:
				_instance = DevStyle.new()
		return _instance

# -- Colors --
@export_group("Colors")
@export var overlay_color: Color = Color(0, 0, 0, 0.55)
@export var panel_bg: Color = Color(0.10, 0.10, 0.12, 0.98)
@export var panel_border: Color = Color(0.30, 0.30, 0.32, 1.0)
@export var header_bg: Color = Color(0.15, 0.15, 0.18, 1.0)
@export var tab_bg_normal: Color = Color(0.15, 0.15, 0.18, 1.0)
@export var tab_bg_hover: Color = Color(0.22, 0.22, 0.26, 1.0)
@export var tab_bg_active: Color = Color(0.30, 0.28, 0.22, 1.0)
@export var tab_border: Color = Color(0.28, 0.28, 0.30, 1.0)
@export var text_primary: Color = Color(1, 1, 1)
@export var text_muted: Color = Color(0.7, 0.7, 0.72)
@export var text_heading: Color = Color(1.0, 0.85, 0.4)
@export var button_close: Color = Color(0.62, 0.20, 0.20)
@export var button_dev_menu: Color = Color(0.30, 0.22, 0.10)
@export var outline_color: Color = Color(0, 0, 0)

# -- Layout: panel --
@export_group("Layout: Panel")
@export_range(0.1, 1.0, 0.01) var panel_width_ratio: float = 0.85
@export_range(0.1, 1.0, 0.01) var panel_height_ratio: float = 0.88
@export var panel_min_width: int = 800
@export var panel_min_height: int = 500
@export var panel_max_width: int = 1600
@export var panel_max_height: int = 1000
@export var panel_content_margin: int = 16
@export var panel_separation: int = 8

# -- Layout: tabs --
@export_group("Layout: Tabs")
@export var tab_height: int = 36
@export var tab_min_width: int = 120
@export var tab_corner_radius: int = 4

# -- Layout: buttons --
@export_group("Layout: Buttons")
@export var button_corner_radius: int = 3
@export var dev_menu_button_width: int = 160
@export var dev_menu_button_height: int = 32
@export var dev_menu_button_margin: int = 8

# -- Fonts --
@export_group("Fonts")
@export var font_size_title: int = 20
@export var font_size_tab: int = 14
@export var font_size_body: int = 14
@export var font_size_small: int = 12
@export var outline_size: int = 2

# -- Style factories --

func make_panel_style() -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = panel_bg
	sb.border_color = panel_border
	sb.border_width_left = 2
	sb.border_width_right = 2
	sb.border_width_top = 2
	sb.border_width_bottom = 2
	sb.corner_radius_top_left = 6
	sb.corner_radius_top_right = 6
	sb.corner_radius_bottom_left = 6
	sb.corner_radius_bottom_right = 6
	return sb

func make_header_style() -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = header_bg
	sb.corner_radius_top_left = 6
	sb.corner_radius_top_right = 6
	sb.content_margin_left = panel_content_margin
	sb.content_margin_right = panel_content_margin
	sb.content_margin_top = 8
	sb.content_margin_bottom = 8
	return sb

func make_tab_style(bg: Color) -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = bg
	sb.border_color = tab_border
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 0
	sb.corner_radius_top_left = tab_corner_radius
	sb.corner_radius_top_right = tab_corner_radius
	sb.content_margin_left = 12
	sb.content_margin_right = 12
	sb.content_margin_top = 6
	sb.content_margin_bottom = 6
	return sb

func make_button_style(base: Color) -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = base
	sb.border_color = base.darkened(0.25)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = button_corner_radius
	sb.corner_radius_top_right = button_corner_radius
	sb.corner_radius_bottom_left = button_corner_radius
	sb.corner_radius_bottom_right = button_corner_radius
	sb.content_margin_left = 12
	sb.content_margin_right = 12
	sb.content_margin_top = 4
	sb.content_margin_bottom = 4
	return sb
