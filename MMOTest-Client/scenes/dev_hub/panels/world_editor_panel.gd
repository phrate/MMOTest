# world_editor_panel.gd
extends DevPanel
## World editor — TODO: hybrid 2D top-down painting + in-game 3D editing,
## with NPC spawn visibility. See Piece 3 for the full build-out.

func _ready() -> void:
	_build_placeholder("world_editor")
