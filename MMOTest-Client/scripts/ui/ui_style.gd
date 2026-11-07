class_name UIStyle
## Shared UI styling for hover infoboxes and right-click action menus so
## they look identical everywhere.

const BG_COLOR      := Color(0.05, 0.05, 0.05, 0.92)
const BORDER_COLOR  := Color(1, 1, 1, 0.15)
const HOVER_COLOR   := Color(1, 1, 1, 0.16)
const TAB_COLOR          := Color(0.12, 0.12, 0.12, 0.95)
const TAB_HOVER_COLOR    := Color(0.20, 0.20, 0.20, 0.95)
const TAB_SELECTED_COLOR := Color(0.30, 0.26, 0.14, 0.95)
const TAB_BORDER_COLOR   := Color(0.30, 0.30, 0.30, 1.0)
const VERB_COLOR    := Color("#4a9eff")
const TEXT_COLOR    := Color(1, 1, 1)
const OUTLINE_COLOR := Color(0, 0, 0)
const FONT_SIZE     := 13
const OUTLINE_SIZE  := 3
const MULTI_WORD_VERBS := ["Talk to", "Pick up", "Trade with", "Bank with", "Follow with", "Give to"]


static func make_panel_style() -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = BG_COLOR
	sb.border_color = BORDER_COLOR
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.content_margin_left = 6
	sb.content_margin_right = 6
	sb.content_margin_top = 3
	sb.content_margin_bottom = 3
	return sb


static func make_menu_hover_style() -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = HOVER_COLOR
	sb.corner_radius_top_left = 2
	sb.corner_radius_top_right = 2
	sb.corner_radius_bottom_left = 2
	sb.corner_radius_bottom_right = 2
	return sb


static func make_tab_style(bg: Color) -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = bg
	sb.border_color = TAB_BORDER_COLOR
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = 3
	sb.corner_radius_top_right = 3
	sb.corner_radius_bottom_left = 3
	sb.corner_radius_bottom_right = 3
	sb.content_margin_left = 4
	sb.content_margin_right = 4
	return sb


static func make_empty_style() -> StyleBoxEmpty:
	return StyleBoxEmpty.new()


static func format_primary_bbcode(display: String) -> String:
	var parts := split_verb(display)
	var verb: String = parts[0]
	var rest: String = parts[1]
	if rest.is_empty():
		return "[color=%s]%s[/color]" % [VERB_COLOR.to_html(false), verb]
	return "[color=%s]%s[/color] %s" % [VERB_COLOR.to_html(false), verb, rest]


static func split_verb(text: String) -> Array:
	for verb in MULTI_WORD_VERBS:
		if text == verb:
			return [verb, ""]
		if text.begins_with(verb + " "):
			return [verb, text.substr(verb.length() + 1)]
	var space_idx := text.find(" ")
	if space_idx < 0:
		return [text, ""]
	return [text.substr(0, space_idx), text.substr(space_idx + 1)]
