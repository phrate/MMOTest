extends CanvasLayer
## DevHub — the central admin development interface.
##
## Opens with Ctrl+E or the top-left "Development Menu" button. Renders a
## centered modal with a tab bar at the top and the active DevPanel below.
## Panel registration lives in DevPanelRegistry so adding/removing tabs is
## a one-file change.
##
## Only instantiated for admin players (game.gd checks Session.is_admin
## before adding to the tree). Every input handler below assumes admin.
##
## Input model:
##   - Ctrl+E      toggle hub open/closed (works whether open or closed,
##                 whether typing in a LineEdit or not — Ctrl+E always means
##                 "toggle the hub" in dev tools)
##   - Ctrl+1..5   jump to a specific tab while hub is open (per registry
##                 "shortcut" field)

signal closed

const LAYER := 50

var _overlay: Control
var _panel_container: PanelContainer
var _tab_bar: HBoxContainer
var _content_holder: Control

var _panels: Dictionary = {}       # id -> DevPanel
var _tab_buttons: Dictionary = {}  # id -> Button
var _current_id := ""

func _ready() -> void:
	layer = LAYER
	_build_ui()
	_build_panels()
	_select_tab(DevPanelRegistry.DEFAULT_TAB)
	_overlay.visible = false

func _input(event: InputEvent) -> void:
	if not Session.is_admin: return
	if not (event is InputEventKey and event.pressed and not event.echo): return

	if event.keycode == KEY_E and event.ctrl_pressed:
		toggle()
		get_viewport().set_input_as_handled()
		return

	if _overlay.visible and event.ctrl_pressed:
		for def in DevPanelRegistry.PANELS:
			if def.get("shortcut") == event.keycode:
				_select_tab(def["id"])
				get_viewport().set_input_as_handled()
				return

# ---------- Public API ----------

func is_open() -> bool:
	return _overlay.visible

func open() -> void:
	if _overlay.visible: return
	_overlay.visible = true
	_notify_panel_opened()

func close() -> void:
	if not _overlay.visible: return
	_notify_panel_closed()
	_overlay.visible = false
	closed.emit()

func toggle() -> void:
	if _overlay.visible: close()
	else: open()

## Open the hub at a specific tab, optionally passing entity context. Used by
## game.gd's admin right-click integration: right-click NPC → [Admin] Edit →
## open_at("npc_editor", "npc", npc_id).
func open_at(panel_id: String, entity_type: String = "", entity_id: int = 0) -> void:
	_select_tab(panel_id)
	open()
	if entity_type != "" and _panels.has(panel_id):
		_panels[panel_id].on_hub_open_for_entity(entity_type, entity_id)

# ---------- UI construction ----------

func _build_ui() -> void:
	_overlay = Control.new()
	_overlay.name = "Overlay"
	_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_overlay.mouse_filter = Control.MOUSE_FILTER_STOP
	_overlay.add_to_group("modal_blockers")
	_overlay.visible = false
	add_child(_overlay)

	var dim := ColorRect.new()
	dim.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	dim.color = DevStyle.OVERLAY_COLOR
	dim.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_overlay.add_child(dim)

	_panel_container = PanelContainer.new()
	_panel_container.name = "Panel"
	_panel_container.anchor_left = 0.5
	_panel_container.anchor_right = 0.5
	_panel_container.anchor_top = 0.5
	_panel_container.anchor_bottom = 0.5
	_panel_container.offset_left = -DevStyle.PANEL_WIDTH / 2.0
	_panel_container.offset_right = DevStyle.PANEL_WIDTH / 2.0
	_panel_container.offset_top = -DevStyle.PANEL_HEIGHT / 2.0
	_panel_container.offset_bottom = DevStyle.PANEL_HEIGHT / 2.0
	_panel_container.add_theme_stylebox_override("panel", DevStyle.make_panel_style())
	_overlay.add_child(_panel_container)

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 0)
	_panel_container.add_child(vbox)

	# Header
	var header := PanelContainer.new()
	header.add_theme_stylebox_override("panel", DevStyle.make_header_style())
	vbox.add_child(header)

	var header_row := HBoxContainer.new()
	header_row.add_theme_constant_override("separation", DevStyle.PANEL_SEPARATION)
	header.add_child(header_row)

	var title := Label.new()
	title.text = Texts.get_text("ui.dev_hub.title")
	title.add_theme_font_size_override("font_size", DevStyle.FONT_SIZE_TITLE)
	title.add_theme_color_override("font_color", DevStyle.TEXT_HEADING)
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	header_row.add_child(title)

	var close_btn := Button.new()
	close_btn.text = Texts.get_text("ui.dev_hub.close")
	close_btn.focus_mode = Control.FOCUS_NONE
	close_btn.add_theme_stylebox_override("normal", DevStyle.make_button_style(DevStyle.BUTTON_CLOSE))
	close_btn.add_theme_stylebox_override("hover", DevStyle.make_button_style(DevStyle.BUTTON_CLOSE.lightened(0.12)))
	close_btn.add_theme_stylebox_override("pressed", DevStyle.make_button_style(DevStyle.BUTTON_CLOSE.darkened(0.12)))
	close_btn.add_theme_color_override("font_color", DevStyle.TEXT_PRIMARY)
	close_btn.add_theme_color_override("font_hover_color", DevStyle.TEXT_PRIMARY)
	close_btn.add_theme_color_override("font_pressed_color", DevStyle.TEXT_PRIMARY)
	close_btn.pressed.connect(close)
	header_row.add_child(close_btn)

	# Tab bar
	_tab_bar = HBoxContainer.new()
	_tab_bar.add_theme_constant_override("separation", 0)
	vbox.add_child(_tab_bar)

	# Content area
	var content_margin := MarginContainer.new()
	content_margin.add_theme_constant_override("margin_left", DevStyle.PANEL_CONTENT_MARGIN)
	content_margin.add_theme_constant_override("margin_right", DevStyle.PANEL_CONTENT_MARGIN)
	content_margin.add_theme_constant_override("margin_top", DevStyle.PANEL_CONTENT_MARGIN)
	content_margin.add_theme_constant_override("margin_bottom", DevStyle.PANEL_CONTENT_MARGIN)
	content_margin.size_flags_vertical = Control.SIZE_EXPAND_FILL
	vbox.add_child(content_margin)

	_content_holder = Control.new()
	_content_holder.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_content_holder.size_flags_vertical = Control.SIZE_EXPAND_FILL
	content_margin.add_child(_content_holder)

# ---------- Panels ----------

func _build_panels() -> void:
	for def in DevPanelRegistry.PANELS:
		var id: String = def["id"]
		var script_path: String = def["script"]
		if not ResourceLoader.exists(script_path):
			push_error("[DevHub] Missing panel script '%s'" % script_path)
			continue
		var panel: DevPanel = load(script_path).new()
		panel.name = id
		panel.visible = false
		panel.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
		_content_holder.add_child(panel)
		_panels[id] = panel

		var btn := Button.new()
		btn.text = Texts.get_text(def["name_key"])
		btn.focus_mode = Control.FOCUS_NONE
		btn.custom_minimum_size = Vector2(DevStyle.TAB_MIN_WIDTH, DevStyle.TAB_HEIGHT)
		btn.add_theme_font_size_override("font_size", DevStyle.FONT_SIZE_TAB)
		btn.add_theme_color_override("font_color", DevStyle.TEXT_PRIMARY)
		btn.add_theme_color_override("font_hover_color", DevStyle.TEXT_PRIMARY)
		btn.add_theme_color_override("font_pressed_color", DevStyle.TEXT_PRIMARY)
		_tab_bar.add_child(btn)
		_tab_buttons[id] = btn
		btn.pressed.connect(_select_tab.bind(id))

# ---------- Tab switching ----------

func _select_tab(id: String) -> void:
	if not _panels.has(id):
		push_warning("[DevHub] Unknown panel id '%s'" % id)
		return
	if id == _current_id and _overlay.visible:
		return

	if _current_id != "" and _panels.has(_current_id):
		var old_panel: DevPanel = _panels[_current_id]
		old_panel.panel_closed()
		old_panel.visible = false

	_current_id = id
	var new_panel: DevPanel = _panels[id]
	new_panel.visible = true
	if _overlay.visible:
		new_panel.panel_opened()

	for tid in _tab_buttons.keys():
		var btn: Button = _tab_buttons[tid]
		var bg: Color = DevStyle.TAB_BG_ACTIVE if tid == id else DevStyle.TAB_BG_NORMAL
		btn.add_theme_stylebox_override("normal", DevStyle.make_tab_style(bg))
		btn.add_theme_stylebox_override("hover", DevStyle.make_tab_style(DevStyle.TAB_BG_HOVER))
		btn.add_theme_stylebox_override("pressed", DevStyle.make_tab_style(DevStyle.TAB_BG_ACTIVE))

func _notify_panel_opened() -> void:
	if _current_id != "" and _panels.has(_current_id):
		_panels[_current_id].panel_opened()

func _notify_panel_closed() -> void:
	if _current_id != "" and _panels.has(_current_id):
		_panels[_current_id].panel_closed()
