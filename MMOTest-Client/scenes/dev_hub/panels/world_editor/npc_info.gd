class_name WorldEditorNpcInfo
extends PanelContainer
## Floating info overlay docked top-right of the world editor canvas.
## Shows: clipboard banner (when a type is copied), selection title (NPC
## display name), selection body (slug + render + tile + delete-queued flag),
## or a "no selection" hint when nothing is selected.

signal clipboard_clear_pressed

const INFO_PANEL_WIDTH := 280.0
const INFO_PANEL_MARGIN := 12.0

var _title: RichTextLabel
var _body: RichTextLabel
var _clipboard_banner: PanelContainer
var _clipboard_slug_label: Label


func _init() -> void:
	_build_ui()


func _build_ui() -> void:
	var s := DevStyle.instance

	set_anchors_preset(Control.PRESET_TOP_RIGHT)
	offset_left = -INFO_PANEL_WIDTH - INFO_PANEL_MARGIN
	offset_right = -INFO_PANEL_MARGIN
	offset_top = INFO_PANEL_MARGIN
	offset_bottom = INFO_PANEL_MARGIN + 220
	mouse_filter = Control.MOUSE_FILTER_STOP

	var panel_style := s.make_panel_style()
	panel_style.bg_color = Color(0.12, 0.12, 0.16, 0.94)
	panel_style.border_color = Color(0.35, 0.35, 0.42, 1.0)
	panel_style.content_margin_left = 12
	panel_style.content_margin_right = 12
	panel_style.content_margin_top = 12
	panel_style.content_margin_bottom = 12
	add_theme_stylebox_override("panel", panel_style)

	var info_vbox := VBoxContainer.new()
	info_vbox.add_theme_constant_override("separation", 8)
	add_child(info_vbox)

	_clipboard_banner = _build_clipboard_banner()
	info_vbox.add_child(_clipboard_banner)

	_title = RichTextLabel.new()
	_title.bbcode_enabled = true
	_title.fit_content = true
	_title.scroll_active = false
	_title.custom_minimum_size = Vector2(INFO_PANEL_WIDTH - 24, 30)
	_title.add_theme_font_size_override("normal_font_size", 18)
	info_vbox.add_child(_title)

	_body = RichTextLabel.new()
	_body.bbcode_enabled = true
	_body.fit_content = true
	_body.scroll_active = false
	_body.custom_minimum_size = Vector2(INFO_PANEL_WIDTH - 24, 60)
	_body.add_theme_font_size_override("normal_font_size", s.font_size_small)
	info_vbox.add_child(_body)


func _build_clipboard_banner() -> PanelContainer:
	var s := DevStyle.instance
	var banner := PanelContainer.new()
	var cb_style := StyleBoxFlat.new()
	cb_style.bg_color = Color(0.22, 0.18, 0.05, 1.0)
	cb_style.border_color = Color(0.85, 0.65, 0.15, 1.0)
	cb_style.border_width_left = 1
	cb_style.border_width_right = 1
	cb_style.border_width_top = 1
	cb_style.border_width_bottom = 1
	cb_style.corner_radius_top_left = 4
	cb_style.corner_radius_top_right = 4
	cb_style.corner_radius_bottom_left = 4
	cb_style.corner_radius_bottom_right = 4
	cb_style.content_margin_left = 10
	cb_style.content_margin_right = 10
	cb_style.content_margin_top = 8
	cb_style.content_margin_bottom = 8
	banner.add_theme_stylebox_override("panel", cb_style)
	banner.visible = false

	var cb_vbox := VBoxContainer.new()
	cb_vbox.add_theme_constant_override("separation", 4)
	banner.add_child(cb_vbox)

	var cb_title := Label.new()
	cb_title.text = Texts.get_text("ui.dev_hub.world_editor.clipboard_banner")
	cb_title.add_theme_font_size_override("font_size", s.font_size_small)
	cb_title.add_theme_color_override("font_color", Color(0.85, 0.65, 0.15))
	cb_title.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.6))
	cb_title.add_theme_constant_override("outline_size", 2)
	cb_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	cb_vbox.add_child(cb_title)

	_clipboard_slug_label = Label.new()
	_clipboard_slug_label.text = ""
	_clipboard_slug_label.add_theme_font_size_override("font_size", 18)
	_clipboard_slug_label.add_theme_color_override("font_color", Color(1.0, 0.85, 0.30))
	_clipboard_slug_label.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.7))
	_clipboard_slug_label.add_theme_constant_override("outline_size", 3)
	_clipboard_slug_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	cb_vbox.add_child(_clipboard_slug_label)

	var clear_btn := Button.new()
	clear_btn.text = Texts.get_text("ui.dev_hub.world_editor.clipboard_clear")
	clear_btn.focus_mode = Control.FOCUS_NONE
	clear_btn.add_theme_font_size_override("font_size", s.font_size_small)
	clear_btn.add_theme_color_override("font_color", Color(1, 1, 1))
	clear_btn.add_theme_color_override("font_hover_color", Color(1, 1, 1))
	clear_btn.add_theme_color_override("font_pressed_color", Color(1, 1, 1))
	clear_btn.add_theme_stylebox_override("normal", s.make_button_style(s.button_close))
	clear_btn.add_theme_stylebox_override("hover", s.make_button_style(s.button_close.lightened(0.12)))
	clear_btn.add_theme_stylebox_override("pressed", s.make_button_style(s.button_close.darkened(0.12)))
	clear_btn.pressed.connect(func() -> void: clipboard_clear_pressed.emit())
	cb_vbox.add_child(clear_btn)

	return banner


# ---------- Public API ----------

func show_selection(npc_data: Dictionary) -> void:
	var slug := str(npc_data.get("slug_id", ""))
	var disp := str(npc_data.get("display", ""))
	var render := str(npc_data.get("render_slug", ""))
	var tx := int(npc_data.get("x", 0))
	var ty := int(npc_data.get("y", 0))
	var deleting := bool(npc_data.get("_deleting", false))

	_title.text = "[b][color=#ffd84a][font_size=22]%s[/font_size][/color][/b]" % disp
	_body.text = (
		"[color=#8a8f96]%s[/color]\n\n"
		+ "[color=#8a8f96]%s[/color]  [color=#ffffff]%s[/color]\n"
		+ "[color=#8a8f96]%s[/color]  [color=#ffffff]%d, %d[/color]"
		+ ("\n\n[color=#ff6060][b]Queued for deletion[/b][/color]" if deleting else "")
	) % [
		slug,
		Texts.get_text("ui.dev_hub.world_editor.npc_info_render"), render,
		Texts.get_text("ui.dev_hub.world_editor.npc_info_tile"), tx, ty,
	]

func show_no_selection() -> void:
	_title.text = ""
	_body.text = "[i][color=#9aa0a8]%s[/color][/i]" % Texts.get_text("ui.dev_hub.world_editor.no_selection")

func show_clipboard(slug: String) -> void:
	_clipboard_banner.visible = true
	_clipboard_slug_label.text = slug

func hide_clipboard() -> void:
	_clipboard_banner.visible = false