class_name ItemEditorForm
extends VBoxContainer
## Full item form (icon / identity / flags / tags / capabilities / references).
## Two instances: one for Edit tab, one for Create tab. Both receive data via
## set_data() and emit their payload via get_payload(). Layout is 30/70.

signal give_requested(target_username: String, amount: int, is_cert: bool)

const ITEM_TYPES := ["weapon","consumable","armor","misc"]
const ICON_BOX_MIN_SIZE := 120
const ICON_BOX_PADDING := 8
const SECTION_SPACING := 8
const REFS_INCLUDE := 12

var _src: Dictionary = {}
var _fields: Dictionary = {}
var _icon_box: PanelContainer
var _tag_input: DevTagInput
var _caps: ItemEditorCapabilities
var _is_edit_mode: bool = false
var _available_tags: Array = []
var _give_panel: ItemEditorGive


func _init() -> void:
	add_theme_constant_override("separation", SECTION_SPACING)


func set_data(src: Dictionary, is_edit_mode: bool) -> void:
	_src = src.duplicate()
	_is_edit_mode = is_edit_mode
	_rebuild()


func _rebuild() -> void:
	for c in get_children():
		remove_child(c)
		c.queue_free()
	_fields.clear()

	# Row 0: icon | identity
	var row0 := HBoxContainer.new()
	row0.add_theme_constant_override("separation", SECTION_SPACING)
	add_child(row0)
	_build_icon_section(row0)
	_build_identity_section(row0)

	# Row 1: give (edit only) | flags
	var row1 := HBoxContainer.new()
	row1.add_theme_constant_override("separation", SECTION_SPACING)
	add_child(row1)
	if _is_edit_mode:
		_build_give_section(row1)
	else:
		_add_blank(row1)
	_build_flags_section(row1)

	# Row 2: blank | tags
	var row2 := HBoxContainer.new()
	row2.add_theme_constant_override("separation", SECTION_SPACING)
	add_child(row2)
	_add_blank(row2)
	_build_tags_section(row2)

	# Row 3: blank | capabilities
	var row3 := HBoxContainer.new()
	row3.add_theme_constant_override("separation", SECTION_SPACING)
	add_child(row3)
	_add_blank(row3)
	_build_capabilities_section(row3)

	# Row 4: references (edit only)
	if _is_edit_mode:
		_build_references_section()


func _add_blank(parent: HBoxContainer) -> void:
	var blank := Control.new()
	blank.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	blank.size_flags_stretch_ratio = 3.0
	parent.add_child(blank)


func _make_section(ratio: float, header_key: String) -> DevSection:
	var sec := DevSection.new()
	sec.set_ratio(ratio)
	sec.add_header(Texts.get_text("ui.dev_hub.item_editor." + header_key))
	return sec


func _build_icon_section(parent: HBoxContainer) -> void:
	var sec := _make_section(3.0, "section_icon")
	_icon_box = PanelContainer.new()
	_icon_box.custom_minimum_size = Vector2(ICON_BOX_MIN_SIZE, ICON_BOX_MIN_SIZE)
	_icon_box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_icon_box.size_flags_vertical = Control.SIZE_EXPAND_FILL
	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.10, 0.10, 0.14, 1.0)
	sb.border_color = Color(0.35, 0.35, 0.42, 1.0)
	sb.border_width_left = 2
	sb.border_width_right = 2
	sb.border_width_top = 2
	sb.border_width_bottom = 2
	sb.content_margin_left = ICON_BOX_PADDING
	sb.content_margin_right = ICON_BOX_PADDING
	sb.content_margin_top = ICON_BOX_PADDING
	sb.content_margin_bottom = ICON_BOX_PADDING
	_icon_box.add_theme_stylebox_override("panel", sb)
	sec.content.add_child(_icon_box)
	_populate_icon()
	parent.add_child(sec)

func _build_give_section(parent: HBoxContainer) -> void:
	var sec := DevSection.new()
	sec.set_ratio(3.0)
	sec.add_header(Texts.get_text("ui.dev_hub.item_editor.give.header"))
	_give_panel = ItemEditorGive.new()
	_give_panel.set_item_data(_src)
	_give_panel.set_current_username(Session.username)
	_give_panel.give_requested.connect(_on_give_requested)
	sec.content.add_child(_give_panel)
	parent.add_child(sec)


func _on_give_requested(target_username: String, amount: int, is_cert: bool) -> void:
	give_requested.emit(target_username, amount, is_cert)


func show_give_result(ok: bool, error_key: String, error_params: Dictionary) -> void:
	if _give_panel != null and is_instance_valid(_give_panel):
		_give_panel.show_result(ok, error_key, error_params)

func _populate_icon() -> void:
	for c in _icon_box.get_children():
		_icon_box.remove_child(c)
		c.queue_free()
	var icon_name := _resolve_icon_name()
	var tex: Texture2D = null
	if not icon_name.is_empty() and ItemIcons:
		tex = ItemIcons.get_texture(icon_name)
	var center := CenterContainer.new()
	center.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	center.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_icon_box.add_child(center)
	if tex == null:
		var s := DevStyle.instance
		var lbl := Label.new()
		lbl.text = Texts.get_text("ui.dev_hub.item_editor.icon_no_icon")
		lbl.add_theme_font_size_override("font_size", s.font_size_small)
		lbl.add_theme_color_override("font_color", s.text_muted)
		lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		lbl.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		center.add_child(lbl)
		return
	var tr := TextureRect.new()
	tr.texture = tex
	tr.custom_minimum_size = tex.get_size()
	tr.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	tr.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	center.add_child(tr)


func _resolve_icon_name() -> String:
	var icon_path := str(_src.get("icon_path", "")).strip_edges()
	if not icon_path.is_empty(): return icon_path
	return str(_src.get("slug_id", "")).strip_edges()


func refresh_icon() -> void:
	if is_instance_valid(_icon_box):
		_populate_icon()


func _build_identity_section(parent: HBoxContainer) -> void:
	var sec := _make_section(7.0, "section_identity")
	var p := "ui.dev_hub.item_editor."

	var icon_path_value := str(_src.get("icon_path", ""))
	var slug := str(_src.get("slug_id", ""))
	var placeholder := ""
	if not slug.is_empty():
		placeholder = "%s.png (uses slug)" % slug

	DevFormFields.line_edit(sec.content, Texts.get_text(p + "label_slug_id"),
		str(_src.get("slug_id", "")), _is_edit_mode, _fields, "slug_id")
	DevFormFields.line_edit(sec.content, Texts.get_text(p + "label_display"),
		str(_src.get("display", "")), false, _fields, "display")
	DevFormFields.textarea(sec.content, Texts.get_text(p + "label_description"),
		str(_src.get("description", "")), _fields, "description")
	DevFormFields.line_edit(sec.content, Texts.get_text(p + "label_icon_path"),
		icon_path_value, false, _fields, "icon_path", placeholder)
	DevFormFields.dropdown(sec.content, Texts.get_text(p + "label_type"),
		str(_src.get("type", "misc")), ITEM_TYPES, _fields, "type")
	DevFormFields.spin_row(sec.content, Texts.get_text(p + "label_base_price"),
		int(_src.get("base_price", 0)), 0, 999999999, _fields, "base_price")
	parent.add_child(sec)


func _build_flags_section(parent: HBoxContainer) -> void:
	var sec := _make_section(7.0, "section_flags")
	var p := "ui.dev_hub.item_editor."
	var flow := HFlowContainer.new()
	flow.add_theme_constant_override("h_separation", 6)
	flow.add_theme_constant_override("v_separation", 6)
	sec.content.add_child(flow)

	_add_toggle(flow, p + "label_is_active", "is_active",
		bool(_src.get("is_active", true)), DevToggleButton.Kind.PURPLE)
	_add_toggle(flow, p + "label_stackable", "stackable",
		bool(_src.get("stackable", false)), DevToggleButton.Kind.GREEN)
	_add_toggle(flow, p + "label_can_trade", "can_trade",
		bool(_src.get("can_trade", true)), DevToggleButton.Kind.GREEN)
	_add_toggle(flow, p + "label_can_bank", "can_bank",
		not bool(_src.get("non_bankable", false)), DevToggleButton.Kind.GREEN)
	_add_toggle(flow, p + "label_can_cert", "can_cert",
		not bool(_src.get("non_certable", false)), DevToggleButton.Kind.GREEN)
	parent.add_child(sec)


func _add_toggle(flow: HFlowContainer, label_key: String, save_key: String,
				 pressed: bool, kind: int) -> void:
	var btn := DevToggleButton.new()
	btn.setup(Texts.get_text(label_key), pressed, kind)
	flow.add_child(btn)
	_fields[save_key] = btn


func _build_tags_section(parent: HBoxContainer) -> void:
	var sec := _make_section(7.0, "section_tags")
	_tag_input = DevTagInput.new()
	_tag_input.set_placeholder(Texts.get_text("ui.dev_hub.item_editor.tags_add_placeholder"))
	_tag_input.set_suggestions(_available_tags)
	var tags: Array = []
	var raw: Variant = _src.get("tags", [])
	if typeof(raw) == TYPE_ARRAY:
		for t in raw: tags.append(str(t))
	_tag_input.set_tags(tags)
	sec.content.add_child(_tag_input)
	parent.add_child(sec)


func _build_capabilities_section(parent: HBoxContainer) -> void:
	var sec := _make_section(7.0, "section_capabilities")
	_caps = ItemEditorCapabilities.new()
	_caps.set_data(_src)
	sec.content.add_child(_caps)
	parent.add_child(sec)


func _build_references_section() -> void:
	var sec := _make_section(1.0, "section_references")
	var s := DevStyle.instance
	var refs := Label.new()
	refs.text = Texts.get_text("ui.dev_hub.item_editor.references_none")
	refs.add_theme_font_size_override("font_size", s.font_size_small)
	refs.add_theme_color_override("font_color", s.text_muted)
	sec.content.add_child(refs)
	add_child(sec)


func get_payload() -> Dictionary:
	var data := DevFormFields.collect_payload(_fields, _src)

	# Inverted toggles
	if _fields.has("can_bank"):
		data["non_bankable"] = not (_fields["can_bank"] as Button).button_pressed
	if _fields.has("can_cert"):
		data["non_certable"] = not (_fields["can_cert"] as Button).button_pressed

	# Tags
	if _tag_input != null:
		data["tags"] = _tag_input.get_tags()
	else:
		data["tags"] = []

	# Capabilities
	if _caps != null:
		var cap_data := _caps.get_data()
		data["equipment"] = cap_data.get("equipment", null)
		data["consumable"] = cap_data.get("consumable", null)

	return data

func set_available_tags(tags: Array) -> void:
	_available_tags = tags.duplicate()
	if _tag_input != null and is_instance_valid(_tag_input):
		_tag_input.set_suggestions(_available_tags)
