class_name DevStyle
## Shared styling for the DevHub and every DevPanel. Colors, spacing, and
## font sizes live here so theming changes touch one file instead of ten.

# -- Colors --
const OVERLAY_COLOR     := Color(0, 0, 0, 0.55)
const PANEL_BG          := Color(0.10, 0.10, 0.12, 0.98)
const PANEL_BORDER      := Color(0.30, 0.30, 0.32, 1.0)
const HEADER_BG         := Color(0.15, 0.15, 0.18, 1.0)
const TAB_BG_NORMAL     := Color(0.15, 0.15, 0.18, 1.0)
const TAB_BG_HOVER      := Color(0.22, 0.22, 0.26, 1.0)
const TAB_BG_ACTIVE     := Color(0.30, 0.28, 0.22, 1.0)
const TAB_BORDER        := Color(0.28, 0.28, 0.30, 1.0)
const TEXT_PRIMARY      := Color(1, 1, 1)
const TEXT_MUTED        := Color(0.7, 0.7, 0.72)
const TEXT_HEADING      := Color(1.0, 0.85, 0.4)
const BUTTON_CLOSE      := Color(0.62, 0.20, 0.20)
const BUTTON_DEV_MENU   := Color(0.30, 0.22, 0.10)
const OUTLINE_COLOR     := Color(0, 0, 0)

# -- Sizes --
const PANEL_WIDTH               := 1200
const PANEL_HEIGHT              := 800
const PANEL_CONTENT_MARGIN      := 16
const PANEL_SEPARATION          := 8
const TAB_HEIGHT                := 36
const TAB_MIN_WIDTH             := 120
const TAB_CORNER_RADIUS         := 4
const BUTTON_CORNER_RADIUS      := 3

# -- Fonts --
const FONT_SIZE_TITLE  := 20
const FONT_SIZE_TAB    := 14
const FONT_SIZE_BODY   := 14
const FONT_SIZE_SMALL  := 12
const OUTLINE_SIZE     := 2

# -- Helpers --
static func make_panel_style() -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = PANEL_BG
	sb.border_color = PANEL_BORDER
	sb.border_width_left = 2
	sb.border_width_right = 2
	sb.border_width_top = 2
	sb.border_width_bottom = 2
	sb.corner_radius_top_left = 6
	sb.corner_radius_top_right = 6
	sb.corner_radius_bottom_left = 6
	sb.corner_radius_bottom_right = 6
	return sb

static func make_header_style() -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = HEADER_BG
	sb.corner_radius_top_left = 6
	sb.corner_radius_top_right = 6
	sb.content_margin_left = PANEL_CONTENT_MARGIN
	sb.content_margin_right = PANEL_CONTENT_MARGIN
	sb.content_margin_top = 8
	sb.content_margin_bottom = 8
	return sb

static func make_tab_style(bg: Color) -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = bg
	sb.border_color = TAB_BORDER
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 0
	sb.corner_radius_top_left = TAB_CORNER_RADIUS
	sb.corner_radius_top_right = TAB_CORNER_RADIUS
	sb.content_margin_left = 12
	sb.content_margin_right = 12
	sb.content_margin_top = 6
	sb.content_margin_bottom = 6
	return sb

static func make_button_style(base: Color) -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = base
	sb.border_color = base.darkened(0.25)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = BUTTON_CORNER_RADIUS
	sb.corner_radius_top_right = BUTTON_CORNER_RADIUS
	sb.corner_radius_bottom_left = BUTTON_CORNER_RADIUS
	sb.corner_radius_bottom_right = BUTTON_CORNER_RADIUS
	sb.content_margin_left = 12
	sb.content_margin_right = 12
	sb.content_margin_top = 4
	sb.content_margin_bottom = 4
	return sb
