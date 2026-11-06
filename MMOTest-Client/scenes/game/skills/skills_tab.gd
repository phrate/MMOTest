class_name SkillsTab
extends SidePanelTab
## Skills tab: a grid of skill cells, an instant hover infobox, and the total level.
## Skills come from the server (Skills autoload), so new skills appear without changing this file.

const COLUMNS := 3

var _grid: GridContainer
var _total_label: Label
var _hover_panel: PanelContainer
var _hover_label: Label
var _hover_id := ""


func _ready() -> void:
	_build_ui()
	Skills.changed.connect(_refresh)
	_refresh()


func tab_deselected() -> void:
	_hover_id = ""


func _process(_delta: float) -> void:
	if _hover_id.is_empty() or not is_visible_in_tree():
		_hover_panel.visible = false
		return

	var viewport_size := get_viewport().get_visible_rect().size
	var mouse := get_viewport().get_mouse_position()
	_hover_panel.reset_size()
	var pos := mouse + Vector2(16, -_hover_panel.size.y - 8)
	pos.x = minf(pos.x, viewport_size.x - _hover_panel.size.x - 4.0)
	pos.y = maxf(pos.y, 4.0)
	_hover_panel.position = pos
	_hover_panel.visible = true


func _build_ui() -> void:
	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 4)
	add_child(vbox)

	_grid = GridContainer.new()
	_grid.columns = COLUMNS
	_grid.add_theme_constant_override("h_separation", 4)
	_grid.add_theme_constant_override("v_separation", 4)
	_grid.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	vbox.add_child(_grid)

	# Fills the leftover height so the total sits at the bottom of the tab.
	_total_label = Label.new()
	_total_label.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_total_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_total_label.vertical_alignment = VERTICAL_ALIGNMENT_BOTTOM
	_total_label.add_theme_font_size_override("font_size", 12)
	vbox.add_child(_total_label)

	# The infobox lives on its own layer so it floats above everything, like the inventory one.
	var hover_layer := CanvasLayer.new()
	hover_layer.layer = 14
	add_child(hover_layer)

	_hover_panel = PanelContainer.new()
	_hover_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_panel.visible = false
	_hover_panel.add_theme_stylebox_override("panel", UIStyle.make_panel_style())
	hover_layer.add_child(_hover_panel)

	_hover_label = Label.new()
	_hover_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_hover_label.add_theme_font_size_override("font_size", UIStyle.FONT_SIZE)
	_hover_label.add_theme_constant_override("outline_size", UIStyle.OUTLINE_SIZE)
	_hover_label.add_theme_color_override("font_outline_color", UIStyle.OUTLINE_COLOR)
	_hover_panel.add_child(_hover_label)


func _refresh() -> void:
	var skills := Skills.all()
	var cells := _grid.get_children()
	var can_update_in_place := cells.size() == skills.size()
	if can_update_in_place:
		for index in range(skills.size()):
			var cell := cells[index] as SkillCell
			if cell == null or cell.skill_id != skills[index]["id"]:
				can_update_in_place = false
				break

	if can_update_in_place:
		for index in range(skills.size()):
			var cell := cells[index] as SkillCell
			cell.update_levels(int(skills[index]["level"]), int(skills[index]["max"]))
		if not _hover_id.is_empty():
			_update_hover_infobox(_hover_id)
	else:
		_hover_id = ""
		for c in cells:
			_grid.remove_child(c)
			c.queue_free()

		for skill: Dictionary in skills:
			var cell := SkillCell.new()
			cell.setup(skill["id"], int(skill["level"]), int(skill["max"]))
			cell.mouse_entered.connect(_on_cell_entered.bind(cell.skill_id))
			cell.mouse_exited.connect(_on_cell_exited.bind(cell.skill_id))
			_grid.add_child(cell)

	_total_label.text = Texts.get_text("game.skills.total_level", {"total": Skills.total_level()})


func _on_cell_entered(id: String) -> void:
	_hover_id = id
	_update_hover_infobox(id)


func _update_hover_infobox(id: String) -> void:
	var skill := Skills.get_skill(id)
	if skill.is_empty():
		return
	var text := Texts.get_text("game.skills.hover", {
		"name": SkillCell.skill_name(id),
		"level": skill["level"],
		"max": skill["max"],
	})
	text += "\n" + Texts.get_text("game.skills.xp", {
		"current_xp": NumberText.with_commas(int(skill["current_xp"])),
	})
	if skill["max_level_reached"]:
		text += "\n" + Texts.get_text("game.skills.max_level")
	else:
		text += "\n" + Texts.get_text("game.skills.xp_to_level", {
			"next_level": skill["next_level"],
			"remaining_xp": NumberText.with_commas(int(skill["remaining_xp"])),
		})
	_hover_label.text = text


func _on_cell_exited(id: String) -> void:
	if _hover_id == id:
		_hover_id = ""
