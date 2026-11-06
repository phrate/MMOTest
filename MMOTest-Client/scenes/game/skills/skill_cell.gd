class_name SkillCell
extends PanelContainer
## One skill in the grid: icon (or a letter until the icon exists) plus "level/max".
## Icons live at res://resources/skills/icons/<skill_id>.png.

const ICON_DIR := "res://resources/skills/icons/"
const CELL_SIZE := Vector2(58, 34)
const ICON_SIZE := 22

var skill_id := ""
var _level_label: Label


static func skill_name(id: String) -> String:
	return Texts.get_text("game.skills.name." + id)


func setup(id: String, level: int, max_level: int) -> void:
	skill_id = id
	custom_minimum_size = CELL_SIZE
	mouse_filter = Control.MOUSE_FILTER_STOP  # so mouse_entered/exited fire for the hover infobox
	add_theme_stylebox_override("panel", _make_style())

	var row := HBoxContainer.new()
	row.alignment = BoxContainer.ALIGNMENT_CENTER
	row.add_theme_constant_override("separation", 4)
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(row)

	row.add_child(_make_icon(id))

	_level_label = Label.new()
	_level_label.add_theme_font_size_override("font_size", 12)
	_level_label.add_theme_constant_override("outline_size", 3)
	_level_label.add_theme_color_override("font_outline_color", Color(0, 0, 0))
	_level_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	row.add_child(_level_label)
	update_levels(level, max_level)


func update_levels(level: int, max_level: int) -> void:
	if _level_label != null:
		_level_label.text = "%d/%d" % [level, max_level]


func _make_icon(id: String) -> Control:
	var path := ICON_DIR + id + ".png"
	if ResourceLoader.exists(path):
		var rect := TextureRect.new()
		rect.texture = load(path)
		rect.custom_minimum_size = Vector2(ICON_SIZE, ICON_SIZE)
		rect.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
		rect.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
		rect.mouse_filter = Control.MOUSE_FILTER_IGNORE
		return rect

	# Placeholder until the icon exists: the first letter of the skill's name.
	var letter := Label.new()
	letter.text = skill_name(id).substr(0, 1).to_upper()
	letter.custom_minimum_size = Vector2(ICON_SIZE, ICON_SIZE)
	letter.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	letter.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	letter.add_theme_font_size_override("font_size", 14)
	letter.add_theme_color_override("font_color", UIStyle.VERB_COLOR)
	letter.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return letter


static func _make_style() -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.12, 0.12, 0.12, 0.95)
	sb.border_color = Color(0.22, 0.22, 0.22, 1.0)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.corner_radius_top_left = 2
	sb.corner_radius_top_right = 2
	sb.corner_radius_bottom_left = 2
	sb.corner_radius_bottom_right = 2
	sb.content_margin_left = 3
	sb.content_margin_right = 3
	sb.content_margin_top = 3
	sb.content_margin_bottom = 3
	return sb
