class_name LootEditorBrowse
extends VBoxContainer
## Paged loot-list browse (2 cols: id, slug). Server-paged, 100/page.
## Selection is click-only — the currently edited row stays highlighted
## until another row is clicked, so the user can see which row they're on
## while editing.

signal entry_selected(id: int)
signal page_changed(page: int)
signal new_requested()

var _search: LineEdit
var _new_btn: Button
var _tree: Tree
var _page_label: Label
var _prev_btn: Button
var _next_btn: Button
var _search_debounce: Timer

var _current_page: int = 0
var _total_count: int = 0
var _page_size: int = 100


func _init() -> void:
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)

	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", s.panel_separation)
	add_child(top)
	_search = LineEdit.new()
	_search.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(_search)
	_new_btn = Button.new()
	_new_btn.focus_mode = Control.FOCUS_NONE
	_new_btn.pressed.connect(func() -> void: new_requested.emit())
	top.add_child(_new_btn)

	_search_debounce = Timer.new()
	_search_debounce.wait_time = 0.3
	_search_debounce.one_shot = true
	add_child(_search_debounce)
	_search.text_changed.connect(func(_t: String) -> void: _search_debounce.start())
	_search_debounce.timeout.connect(_on_search_committed)
	_search.text_submitted.connect(func(_t: String) -> void:
		_search_debounce.stop()
		_on_search_committed())

	_tree = Tree.new()
	_tree.hide_root = true
	_tree.scroll_horizontal_enabled = false
	_tree.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_tree.select_mode = Tree.SELECT_ROW
	add_child(_tree)
	_tree.gui_input.connect(_on_tree_input)
	_tree.mouse_exited.connect(func() -> void: _tree.mouse_default_cursor_shape = Control.CURSOR_ARROW)

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


func configure(col_id: int, col_slug: int, new_button_text: String) -> void:
	_tree.columns = 2
	_tree.column_titles_visible = true
	_tree.set_column_title(0, Texts.get_text("ui.dev_hub.loot_editor.col_id"))
	_tree.set_column_title(1, Texts.get_text("ui.dev_hub.loot_editor.col_slug"))
	_tree.set_column_expand(0, false)
	_tree.set_column_expand(1, true)
	_tree.set_column_custom_minimum_width(0, col_id)
	_tree.set_column_custom_minimum_width(1, col_slug)
	_tree.set_column_title_alignment(0, HORIZONTAL_ALIGNMENT_CENTER)
	_tree.set_column_title_alignment(1, HORIZONTAL_ALIGNMENT_LEFT)
	_search.placeholder_text = Texts.get_text("ui.dev_hub.loot_editor.search_placeholder")
	_prev_btn.text = Texts.get_text("ui.dev_hub.loot_editor.page_prev")
	_next_btn.text = Texts.get_text("ui.dev_hub.loot_editor.page_next")
	_new_btn.text = new_button_text


func get_search_text() -> String: return _search.text.strip_edges()
func get_current_page() -> int: return _current_page


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
		row.set_text(1, str(it.get("slug_id", "")))
		row.set_text_alignment(0, HORIZONTAL_ALIGNMENT_CENTER)
		row.set_text_alignment(1, HORIZONTAL_ALIGNMENT_LEFT)
		row.set_metadata(0, id)
		row.set_custom_color(1, gold)
	_update_pagination()


func select_by_id(id: int) -> void:
	var root := _tree.get_root()
	if root == null: return
	var child := root.get_first_child()
	while child != null:
		if int(child.get_metadata(0)) == id:
			_tree.set_selected(child, 0)
			return
		child = child.get_next()


func update_row_slug(id: int, new_slug: String) -> void:
	var root := _tree.get_root()
	if root == null: return
	var child := root.get_first_child()
	while child != null:
		if int(child.get_metadata(0)) == id:
			child.set_text(1, new_slug)
			return
		child = child.get_next()


func _update_pagination() -> void:
	_prev_btn.disabled = _current_page <= 0
	var total_pages := maxi(1, ceili(float(_total_count) / float(_page_size)))
	_next_btn.disabled = _current_page + 1 >= total_pages
	if _total_count == 0:
		_page_label.text = Texts.get_text("ui.dev_hub.loot_editor.empty")
		return
	var start := _current_page * _page_size + 1
	var end := mini(_current_page * _page_size + _page_size, _total_count)
	_page_label.text = Texts.get_text("ui.dev_hub.loot_editor.page_label", {
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
		_tree.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND if item != null else Control.CURSOR_ARROW
	elif event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
			var item := _tree.get_item_at_position(_tree.get_local_mouse_position())
			if item != null:
				_tree.set_selected(item, 0)
				var md = item.get_metadata(0)
				if md != null:
					entry_selected.emit(int(md))
