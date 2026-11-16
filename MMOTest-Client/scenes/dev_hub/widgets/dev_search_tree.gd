class_name DevSearchTree
extends VBoxContainer
## Searchable Tree with N columns. Emits `row_selected(metadata)` on click.
## Callers add extra toolbar widgets (buttons, toggles) via `toolbar_extras`.

signal row_selected(metadata: Variant)

var search: LineEdit
var toolbar_extras: HBoxContainer
var tree: Tree

var _column_meta: Array = []


func _init() -> void:
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)

	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", s.panel_separation)
	add_child(top)

	search = LineEdit.new()
	search.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	search.text_changed.connect(func(_t: String) -> void: _apply_filter())
	top.add_child(search)

	toolbar_extras = HBoxContainer.new()
	toolbar_extras.add_theme_constant_override("separation", s.panel_separation)
	top.add_child(toolbar_extras)

	tree = Tree.new()
	tree.hide_root = true
	tree.scroll_horizontal_enabled = false
	tree.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	tree.select_mode = Tree.SELECT_ROW
	tree.gui_input.connect(_on_tree_input)
	tree.mouse_exited.connect(func() -> void: tree.mouse_default_cursor_shape = Control.CURSOR_ARROW)
	add_child(tree)


func setup(placeholder: String, columns: Array) -> void:
	## columns: [{title: String, width: int, align: int, expand: bool}]
	search.placeholder_text = placeholder
	_column_meta = columns.duplicate()
	tree.columns = columns.size()
	tree.column_titles_visible = true
	for i in columns.size():
		var c: Dictionary = columns[i]
		tree.set_column_title(i, str(c.get("title", "")))
		tree.set_column_expand(i, bool(c.get("expand", false)))
		tree.set_column_custom_minimum_width(i, int(c.get("width", 100)))
		tree.set_column_title_alignment(i, int(c.get("align", HORIZONTAL_ALIGNMENT_LEFT)))


func set_rows(rows: Array) -> void:
	## rows: [{values: Array[String], metadata: Variant, colors: Dictionary<int, Color>}]
	tree.clear()
	var root := tree.create_item()
	for r in rows:
		if typeof(r) != TYPE_DICTIONARY: continue
		var row := tree.create_item(root)
		var vals: Array = r.get("values", [])
		for i in mini(vals.size(), tree.columns):
			row.set_text(i, str(vals[i]))
			var c: Dictionary = _column_meta[i] if i < _column_meta.size() else {}
			row.set_text_alignment(i, int(c.get("align", HORIZONTAL_ALIGNMENT_LEFT)))
		row.set_metadata(0, r.get("metadata", null))
		var colors: Dictionary = r.get("colors", {})
		for col_idx in colors.keys():
			row.set_custom_color(int(col_idx), colors[col_idx])
	_apply_filter()


func select_by_metadata(metadata: Variant) -> void:
	var root := tree.get_root()
	if root == null: return
	var child := root.get_first_child()
	while child != null:
		if child.get_metadata(0) == metadata:
			tree.set_selected(child, 0)
			return
		child = child.get_next()


func _apply_filter() -> void:
	var filter := search.text.strip_edges().to_lower()
	var root := tree.get_root()
	if root == null: return
	var child := root.get_first_child()
	while child != null:
		var my_match := filter.is_empty()
		if not my_match:
			for i in tree.columns:
				if child.get_text(i).to_lower().contains(filter):
					my_match = true
					break
		child.visible = my_match
		child = child.get_next()


func _on_tree_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion:
		var item := tree.get_item_at_position(tree.get_local_mouse_position())
		if item != null:
			tree.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
			if tree.get_selected() != item:
				tree.set_selected(item, 0)
		else:
			tree.mouse_default_cursor_shape = Control.CURSOR_ARROW
	elif event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
			var item := tree.get_item_at_position(tree.get_local_mouse_position())
			if item != null:
				var md = item.get_metadata(0)
				if md != null:
					row_selected.emit(md)