class_name ItemEditorBrowse
extends VBoxContainer

signal item_selected(item_id: int)
signal create_requested()
signal refresh_requested(include_inactive: bool)

const GOLD := Color(1.0, 0.85, 0.30)

var _tree: DevSearchTree
var _show_inactive: Button


func _init() -> void:
	name = "Browse"
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)

	_tree = DevSearchTree.new()
	add_child(_tree)
	_tree.setup(Texts.get_text("ui.dev_hub.item_editor.browse_search_placeholder"), [
		{"title": Texts.get_text("ui.dev_hub.item_editor.browse_col_slug"), "width": 160, "align": HORIZONTAL_ALIGNMENT_LEFT, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.item_editor.browse_col_display"), "width": 220, "align": HORIZONTAL_ALIGNMENT_LEFT, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.item_editor.browse_col_type"), "width": 90, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.item_editor.browse_col_stacks"), "width": 60, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.item_editor.browse_col_trade"), "width": 60, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.item_editor.browse_col_price"), "width": 80, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.item_editor.browse_col_tags"), "width": 100, "align": HORIZONTAL_ALIGNMENT_LEFT, "expand": true},
	])
	_tree.row_selected.connect(func(md): if md != null: item_selected.emit(int(md)))

	_show_inactive = Button.new()
	_show_inactive.toggle_mode = true
	_show_inactive.focus_mode = Control.FOCUS_NONE
	_show_inactive.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	_show_inactive.text = Texts.get_text("ui.dev_hub.item_editor.browse_show_inactive")
	_show_inactive.add_theme_font_size_override("font_size", s.font_size_small)
	_show_inactive.add_theme_color_override("font_color", s.text_primary)
	_show_inactive.toggled.connect(func(p: bool) -> void: refresh_requested.emit(p))
	_tree.toolbar_extras.add_child(_show_inactive)

	var create_btn := Button.new()
	create_btn.text = Texts.get_text("ui.dev_hub.item_editor.browse_create_button")
	create_btn.focus_mode = Control.FOCUS_NONE
	create_btn.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	create_btn.pressed.connect(func() -> void: create_requested.emit())
	_tree.toolbar_extras.add_child(create_btn)


func get_include_inactive() -> bool:
	return _show_inactive.button_pressed


func set_items(items: Array) -> void:
	var rows: Array = []
	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var iid := int(it.get("id", 0))
		if iid == 0: continue
		var active := bool(it.get("is_active", true))
		var tags: Array = it.get("tags", [])
		var colors := {0: GOLD}
		if not active:
			var dim := Color(GOLD.r * 0.6, GOLD.g * 0.6, GOLD.b * 0.6)
			colors = {0: dim, 1: dim, 2: dim, 3: dim, 4: dim, 5: dim, 6: dim}
		rows.append({
			"values": [
				str(it.get("slug_id", "")),
				str(it.get("display", "")),
				str(it.get("type", "")),
				"✔" if bool(it.get("stackable", false)) else "❌",
				"✔" if bool(it.get("can_trade", true)) else "❌",
				str(int(it.get("base_price", 0))),
				", ".join(tags),
			],
			"metadata": iid,
			"colors": colors,
		})
	_tree.set_rows(rows)