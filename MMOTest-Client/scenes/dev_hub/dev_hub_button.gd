extends CanvasLayer
## Top-left "Development Menu" button. Only added to the tree for admins
## (game.gd checks Session.is_admin). Emits open_requested when clicked;
## game.gd connects it to DevHub.open().

signal open_requested

const LAYER := 9
const MARGIN := 8
const BUTTON_WIDTH := 160
const BUTTON_HEIGHT := 32

func _ready() -> void:
	layer = LAYER

	var btn := Button.new()
	btn.name = "Button"
	btn.text = Texts.get_text("ui.dev_hub.button")
	btn.focus_mode = Control.FOCUS_NONE
	btn.anchor_left = 0.0
	btn.anchor_right = 0.0
	btn.anchor_top = 0.0
	btn.anchor_bottom = 0.0
	btn.offset_left = MARGIN
	btn.offset_top = MARGIN
	btn.offset_right = MARGIN + BUTTON_WIDTH
	btn.offset_bottom = MARGIN + BUTTON_HEIGHT
	btn.add_theme_font_size_override("font_size", DevStyle.FONT_SIZE_TAB)
	btn.add_theme_color_override("font_color", DevStyle.TEXT_PRIMARY)
	btn.add_theme_color_override("font_hover_color", DevStyle.TEXT_PRIMARY)
	btn.add_theme_color_override("font_pressed_color", DevStyle.TEXT_PRIMARY)
	btn.add_theme_stylebox_override("normal", DevStyle.make_button_style(DevStyle.BUTTON_DEV_MENU))
	btn.add_theme_stylebox_override("hover", DevStyle.make_button_style(DevStyle.BUTTON_DEV_MENU.lightened(0.12)))
	btn.add_theme_stylebox_override("pressed", DevStyle.make_button_style(DevStyle.BUTTON_DEV_MENU.darkened(0.12)))
	btn.pressed.connect(func(): open_requested.emit())
	add_child(btn)
