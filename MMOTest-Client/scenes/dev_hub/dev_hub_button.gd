extends CanvasLayer
## Top-left "Development Menu" button. Only added to the tree for admins
## (game.gd checks Session.is_admin). Emits open_requested when clicked;
## game.gd connects it to DevHub.open().

signal open_requested

const LAYER := 9

func _ready() -> void:
	layer = LAYER
	var style := DevStyle.instance

	var btn := Button.new()
	btn.name = "Button"
	btn.text = Texts.get_text("ui.dev_hub.button")
	btn.focus_mode = Control.FOCUS_NONE
	btn.anchor_left = 0.0
	btn.anchor_right = 0.0
	btn.anchor_top = 0.0
	btn.anchor_bottom = 0.0
	btn.offset_left = style.dev_menu_button_margin
	btn.offset_top = style.dev_menu_button_margin
	btn.offset_right = style.dev_menu_button_margin + style.dev_menu_button_width
	btn.offset_bottom = style.dev_menu_button_margin + style.dev_menu_button_height
	btn.add_theme_font_size_override("font_size", style.font_size_tab)
	btn.add_theme_color_override("font_color", style.text_primary)
	btn.add_theme_color_override("font_hover_color", style.text_primary)
	btn.add_theme_color_override("font_pressed_color", style.text_primary)
	btn.add_theme_stylebox_override("normal", style.make_button_style(style.button_dev_menu))
	btn.add_theme_stylebox_override("hover", style.make_button_style(style.button_dev_menu.lightened(0.12)))
	btn.add_theme_stylebox_override("pressed", style.make_button_style(style.button_dev_menu.darkened(0.12)))
	btn.pressed.connect(func(): open_requested.emit())
	add_child(btn)
