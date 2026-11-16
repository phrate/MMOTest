class_name NpcEditorBrowse
extends VBoxContainer
## Type list with search. Single-click emits type_selected; Create button
## emits create_requested.

signal type_selected(type_id: int)
signal create_requested()

const GOLD := Color(1.0, 0.85, 0.30)

var _tree: DevSearchTree


func _init() -> void:
	name = "Browse"
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	var s := DevStyle.instance
	add_theme_constant_override("separation", s.panel_separation)

	_tree = DevSearchTree.new()
	add_child(_tree)
	_tree.setup(Texts.get_text("ui.dev_hub.npc_editor.browse_search_placeholder"), [
		{"title": Texts.get_text("ui.dev_hub.npc_editor.browse_col_slug"), "width": 180, "align": HORIZONTAL_ALIGNMENT_LEFT, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.npc_editor.browse_col_display"), "width": 220, "align": HORIZONTAL_ALIGNMENT_LEFT, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.npc_editor.browse_col_render"), "width": 140, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": false},
		{"title": Texts.get_text("ui.dev_hub.npc_editor.browse_col_instances"), "width": 90, "align": HORIZONTAL_ALIGNMENT_CENTER, "expand": true},
	])
	_tree.row_selected.connect(func(md): if md != null: type_selected.emit(int(md)))

	var create_btn := Button.new()
	create_btn.text = Texts.get_text("ui.dev_hub.npc_editor.browse_create_button")
	create_btn.focus_mode = Control.FOCUS_NONE
	create_btn.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	create_btn.pressed.connect(func() -> void: create_requested.emit())
	_tree.toolbar_extras.add_child(create_btn)


func set_types(items: Array) -> void:
	var rows: Array = []
	for it in items:
		if typeof(it) != TYPE_DICTIONARY: continue
		var tid := int(it.get("id", 0))
		if tid == 0: continue
		rows.append({
			"values": [
				str(it.get("slug_id", "")),
				str(it.get("display", "")),
				str(it.get("render_slug", "")),
				str(int(it.get("instance_count", 0))),
			],
			"metadata": tid,
			"colors": {0: GOLD},
		})
	_tree.set_rows(rows)