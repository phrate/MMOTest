class_name PlayerEditorBrowse
extends VBoxContainer
## Paged player browse list. Server-driven search + pagination — this widget
## holds at most 20 rows at a time and never queries the DB directly.
##
## Differs from DevSearchTree because search is server-side (no client-side
## filter to hide rows we don't have), and pagination is explicit.

signal player_selected(player_id: int)
signal page_changed(page: int)

var _search: LineEdit
var _tree: Tree
var _page_label: Label
var _prev_btn: Button
var _next_btn: Button
var _search_debounce: Timer

var _columns: Array = []
var _current_page: int = 0
var _total_count: int = 0
var _page_size: int = 20


func _init() -> void:
	name = "Browse"
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)

	# Search bar (with debounce)
	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", s.panel_separation)
	add_child(top)
	_search = LineEdit.new()
	_search.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(_search)

	_search_debounce = Timer.new()
	_search_debounce.wait_time = 0.3
	_search_debounce.one_shot = true
	add_child(_search_debounce)
	_search.text_changed.connect(func(_t: String) -> void: _search_debounce.start())
	_search_debounce.timeout.connect(_on_search_committed)
	_search.text_submitted.connect(func(_t: String) -> void:
		_search_debounce.stop()
		_on_search_committed())

	# Tree
	_tree = Tree.new()
	_tree.hide_root = true
	_tree.scroll_horizontal_enabled = false
	_tree.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_tree.select_mode = Tree.SELECT_ROW
	add_child(_tree)
	_tree.gui_input.connect(_on_tree_input)
	_tree.mouse_exited.connect(func() -> void: _tree.mouse_default_cursor_shape = Control.CURSOR_ARROW)

	# Pagination row
	var nav := HBoxContainer.new()
	nav.add_theme_constant_override("separation", s.panel_separation)
	add_child(nav)
	_prev_btn = Button.new()
	_prev_btn.focus_mode = Control.FOCUS_NONE
	_prev_btn.pressed.connect(_on_prev)
	nav.add_child(_prev_btn)
	_page_label = Label.new()
	_page_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_page_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_page_label.add_theme_font_size_override("font_size", s.font_size_small)
	_page_label.add_theme_color_override("font_color", s.text_muted)
	nav.add_child(_page_label)
	_next_btn = Button.new()
	_next_btn.focus_mode = Control.FOCUS_NONE
	_next_btn.pressed.connect(_on_next)
	nav.add_child(_next_btn)


func configure(col_id: int, col_username: int, col_final_level: int, col_credits: int) -> void:
	_columns = [
		{"title": Texts.get_text("ui.dev_hub.player_editor.browse_col_id"),
		 "width": col_id, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.player_editor.browse_col_username"),
		 "width": col_username, "align": HORIZONTAL_ALIGNMENT_LEFT, "expand": true},
		{"title": Texts.get_text("ui.dev_hub.player_editor.browse_col_final_level"),
		 "width": col_final_level, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.player_editor.browse_col_credits"),
		 "width": col_credits, "align": HORIZONTAL_ALIGNMENT_RIGHT, "expand": false},
	]
	_tree.columns = _columns.size()
	_tree.column_titles_visible = true
	for i in _columns.size():
		var c: Dictionary = _columns[i]
		_tree.set_column_title(i, str(c["title"]))
		_tree.set_column_expand(i, bool(c["expand"]))
		_tree.set_column_custom_minimum_width(i, int(c["width"]))
		_tree.set_column_title_alignment(i, int(c["align"]))

	_search.placeholder_text = Texts.get_text("ui.dev_hub.player_editor.browse_search_placeholder")
	_prev_btn.text = Texts.get_text("ui.dev_hub.player_editor.browse_page_prev")
	_next_btn.text = Texts.get_text("ui.dev_hub.player_editor.browse_page_next")


func get_search_text() -> String:
	return _search.text.strip_edges()


func get_current_page() -> int:
	return _current_page


func get_total_count() -> int:
	return _total_count


func set_page(items: Array, page: int, total_count: int, page_size: int) -> void:
	_current_page = page
	_total_count = total_count
	_page_size = page_size

	_tree.clear()
	var root := _tree.create_item()
	var gold := Color(1.0, 0.85, 0.30)

	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var id := int(it.get("id", 0))
		if id <= 0: continue
		var row := _tree.create_item(root)
		row.set_text(0, str(id))
		row.set_text(1, str(it.get("username", "")))
		row.set_text(2, str(int(it.get("final_level", 1))))
		row.set_text(3, NumberText.with_commas(int(it.get("credits", 0))))
		row.set_text_alignment(0, HORIZONTAL_ALIGNMENT_CENTER)
		row.set_text_alignment(1, HORIZONTAL_ALIGNMENT_LEFT)
		row.set_text_alignment(2, HORIZONTAL_ALIGNMENT_CENTER)
		row.set_text_alignment(3, HORIZONTAL_ALIGNMENT_RIGHT)
		row.set_metadata(0, id)
		row.set_custom_color(1, gold)

	_update_pagination()


func _update_pagination() -> void:
	_prev_btn.disabled = _current_page <= 0
	var total_pages := maxi(1, ceili(float(_total_count) / float(_page_size)))
	_next_btn.disabled = _current_page + 1 >= total_pages
	if _total_count == 0:
		_page_label.text = Texts.get_text("ui.dev_hub.player_editor.browse_empty")
		return
	var start := _current_page * _page_size + 1
	var end := mini(_current_page * _page_size + _page_size, _total_count)
	_page_label.text = Texts.get_text("ui.dev_hub.player_editor.browse_page_label", {
		"page": _current_page + 1,
		"start": start,
		"end": end,
		"total": _total_count,
	})


func _on_prev() -> void:
	if _current_page <= 0: return
	_current_page -= 1
	page_changed.emit(_current_page)


func _on_next() -> void:
	var total_pages := maxi(1, ceili(float(_total_count) / float(_page_size)))
	if _current_page + 1 >= total_pages: return
	_current_page += 1
	page_changed.emit(_current_page)


func _on_search_committed() -> void:
	_current_page = 0
	page_changed.emit(0)


func _on_tree_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion:
		var item := _tree.get_item_at_position(_tree.get_local_mouse_position())
		if item != null:
			_tree.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
			if _tree.get_selected() != item:
				_tree.set_selected(item, 0)
		else:
			_tree.mouse_default_cursor_shape = Control.CURSOR_ARROW
	elif event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
			var item := _tree.get_item_at_position(_tree.get_local_mouse_position())
			if item != null:
				var md = item.get_metadata(0)
				if md != null:
					player_selected.emit(int(md))
