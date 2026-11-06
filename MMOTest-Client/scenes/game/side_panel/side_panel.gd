class_name SidePanel
extends CanvasLayer
## Bottom-right HUD column: the credits bar (always visible), the active tab's content,
## and the row of tab buttons. It knows nothing about specific tabs; see SidePanelTabs.

var _tab_container: TabContainer
var _tab_row: HBoxContainer
var _tabs: Array = []  # [{id, content, button}, ...] in SidePanelTabs.TABS order
var _current_id := ""
var _credits_label: Label


func _ready() -> void:
	layer = 10
	_build_ui()
	_build_tabs()
	Session.changed.connect(_refresh_credits)
	_refresh_credits()
	select_tab(SidePanelTabs.DEFAULT_TAB)


func select_tab(id: String) -> void:
	var index := -1
	for i in _tabs.size():
		if _tabs[i]["id"] == id:
			index = i
	if index < 0:
		push_warning("SidePanel: unknown tab '%s'" % id)
		return

	if id != _current_id:
		for t: Dictionary in _tabs:
			if t["id"] == _current_id:
				(t["content"] as SidePanelTab).tab_deselected()
		_tab_container.current_tab = index
		_current_id = id
		(_tabs[index]["content"] as SidePanelTab).tab_selected()

	# Clicking the selected button toggles it off; this puts every button back in sync.
	for t: Dictionary in _tabs:
		(t["button"] as Button).set_pressed_no_signal(t["id"] == id)


# ---------- Construction ----------

func _build_ui() -> void:
	var panel := PanelContainer.new()
	panel.name = "SidePanel"
	panel.anchor_left = 1.0
	panel.anchor_right = 1.0
	panel.anchor_top = 1.0
	panel.anchor_bottom = 1.0
	panel.offset_left = -8.0
	panel.offset_right = -8.0
	panel.offset_top = -8.0
	panel.offset_bottom = -8.0
	panel.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	panel.grow_vertical = Control.GROW_DIRECTION_BEGIN

	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.08, 0.08, 0.08, 0.85)
	sb.border_color = Color(0.22, 0.22, 0.22, 1.0)
	sb.border_width_left = 1
	sb.border_width_right = 1
	sb.border_width_top = 1
	sb.border_width_bottom = 1
	sb.content_margin_left = 4
	sb.content_margin_right = 4
	sb.content_margin_top = 4
	sb.content_margin_bottom = 4
	panel.add_theme_stylebox_override("panel", sb)
	add_child(panel)

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 4)
	panel.add_child(vbox)

	vbox.add_child(_make_credits_bar())

	# Our own icon row, right below the credits bar.
	_tab_row = HBoxContainer.new()
	_tab_row.add_theme_constant_override("separation", 4)
	vbox.add_child(_tab_row)

	# TabContainer with its own tab bar hidden; the row above does the switching.
	# use_hidden_tabs_for_min_size keeps the panel the same size on every tab.
	_tab_container = TabContainer.new()
	_tab_container.tabs_visible = false
	_tab_container.use_hidden_tabs_for_min_size = true
	_tab_container.add_theme_stylebox_override("panel", StyleBoxEmpty.new())
	vbox.add_child(_tab_container)


func _make_credits_bar() -> PanelContainer:
	var credits_box := PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.12, 0.10, 0.05, 0.95)
	style.border_color = Color(0.35, 0.28, 0.10, 1.0)
	style.border_width_left = 1
	style.border_width_right = 1
	style.border_width_top = 1
	style.border_width_bottom = 1
	style.content_margin_left = 6
	style.content_margin_right = 6
	style.content_margin_top = 3
	style.content_margin_bottom = 3
	credits_box.add_theme_stylebox_override("panel", style)

	_credits_label = Label.new()
	_credits_label.text = "Ȼ: 0"
	_credits_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_credits_label.add_theme_font_size_override("font_size", 14)
	_credits_label.modulate = Color(1.0, 0.9, 0.5)
	_credits_label.add_theme_constant_override("outline_size", 2)
	_credits_label.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.7))
	credits_box.add_child(_credits_label)
	return credits_box


func _build_tabs() -> void:
	for def: Dictionary in SidePanelTabs.TABS:
		var id: String = def["id"]
		var content: SidePanelTab = load(def["script"]).new()
		content.name = id
		_tab_container.add_child(content)

		var button := _make_tab_button(id, def)
		_tab_row.add_child(button)
		_tabs.append({"id": id, "content": content, "button": button})


func _make_tab_button(id: String, def: Dictionary) -> Button:
	var button := Button.new()
	button.toggle_mode = true
	button.focus_mode = Control.FOCUS_NONE
	button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	button.custom_minimum_size = Vector2(0, 34)
	button.tooltip_text = Texts.get_text(str(def["name_key"]))

	var icon_path := str(def["icon"])
	if ResourceLoader.exists(icon_path):
		button.icon = load(icon_path)
		button.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
		button.add_theme_constant_override("icon_max_width", 24)
	else:
		button.text = Texts.get_text(str(def["short_key"]))  # placeholder until the icon exists
		button.add_theme_font_size_override("font_size", UIStyle.FONT_SIZE)

	button.add_theme_stylebox_override("normal", UIStyle.make_tab_style(UIStyle.TAB_COLOR))
	button.add_theme_stylebox_override("hover", UIStyle.make_tab_style(UIStyle.TAB_HOVER_COLOR))
	button.add_theme_stylebox_override("pressed", UIStyle.make_tab_style(UIStyle.TAB_SELECTED_COLOR))
	button.add_theme_stylebox_override("hover_pressed", UIStyle.make_tab_style(UIStyle.TAB_SELECTED_COLOR))
	button.pressed.connect(select_tab.bind(id))
	return button


func _refresh_credits() -> void:
	if _credits_label == null:
		return
	_credits_label.text = "Ȼ: %s" % NumberText.with_commas(Session.credits)
