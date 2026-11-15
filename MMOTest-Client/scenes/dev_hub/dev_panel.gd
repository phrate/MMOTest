class_name DevPanel
extends Control
## Base class for every DevHub tab panel. Subclasses override the hooks below
## to react to visibility changes (refresh data, pause animations, etc.).
##
## The base class also provides `_build_placeholder()` — the standard
## "under construction" content that stub panels use.

## Called when this panel becomes the visible tab in an open hub.
func panel_opened() -> void:
	pass

## Called when another panel replaces this one, or the hub closes.
func panel_closed() -> void:
	pass

## Called by DevHub.open_at() when the hub is opened with entity context —
## e.g., right-click an NPC → [Admin] Edit → hub opens at NPC panel with that id.
## Base implementation ignores; subclasses that support context override.
func on_hub_open_for_entity(entity_type: String, entity_id: int) -> void:
	pass

## Standard "under construction" placeholder. Call from _ready() in stub panels.
func _build_placeholder(panel_id: String) -> void:
	var vbox := VBoxContainer.new()
	vbox.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	vbox.alignment = BoxContainer.ALIGNMENT_CENTER
	vbox.add_theme_constant_override("separation", 12)
	add_child(vbox)

	var heading := Label.new()
	heading.text = Texts.get_text("ui.dev_hub.placeholder.heading")
	heading.add_theme_font_size_override("font_size", DevStyle.FONT_SIZE_TITLE)
	heading.add_theme_color_override("font_color", DevStyle.TEXT_HEADING)
	heading.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	vbox.add_child(heading)

	var body := Label.new()
	body.text = Texts.get_text("ui.dev_hub.placeholder.body", {"panel": panel_id.capitalize()})
	body.add_theme_font_size_override("font_size", DevStyle.FONT_SIZE_BODY)
	body.add_theme_color_override("font_color", DevStyle.TEXT_MUTED)
	body.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	vbox.add_child(body)
